using System.Net;
using System.Net.Http.Json;
using Ostatok.Api.Domain;
using Ostatok.Api.Features;

namespace Ostatok.Tests;

/// <summary>
/// Приёмка модуля «Зал»: сценарии из техзадания через HTTP на настоящем PostgreSQL. Каждый тест заводит свой ресторан
/// со своей схемой; часы общие для класса и только идут вперёд, поэтому время броней считается от текущего момента.
/// </summary>
public class HallTests(ApiFixture fx) : IClassFixture<ApiFixture>
{
    private static readonly TimeSpan Zone = TimeSpan.FromHours(3);   // Stock:UtcOffsetHours в настройках

    private sealed record Floor(HttpClient Manager, HttpClient Waiter, Guid Zone, Guid Two, Guid Four, Guid Six);

    private DateTimeOffset Now => fx.Time.GetUtcNow();
    private DateTimeOffset LocalNow => Now.ToOffset(Zone);
    /// <summary>Завтрашний день ресторана: брони на него заведомо в будущем.</summary>
    private DateOnly Tomorrow => DateOnly.FromDateTime(LocalNow.Date).AddDays(1);

    private static async Task<T> Read<T>(HttpResponseMessage response) => (await response.Content.ReadFromJsonAsync<T>(ApiFixture.Json))!;

    /// <summary>Ресторан с одной зоной и столами на 2, 4 и 6 мест; управляющий и официант этого ресторана.</summary>
    private async Task<Floor> FloorAsync()
    {
        var (manager, _, _) = await fx.NewRestaurantAsync($"Зал {Guid.NewGuid():N}"[..20]);
        var login = $"w{Guid.NewGuid():N}"[..16];
        (await manager.PostAsJsonAsync("/api/users", new StaffRequest(login, "Waiter-pass-2026", "Официант", Role.Waiter), ApiFixture.Json)).EnsureSuccessStatusCode();
        var waiter = await fx.SignInAsync(login, "Waiter-pass-2026");

        var zone = await Read<ZoneView>(await Ensure(manager.PostAsJsonAsync("/api/hall/zones", new ZoneRequest("Основной зал"), ApiFixture.Json)));
        async Task<Guid> Table(int number, int seats) =>
            (await Read<TableRef>(await Ensure(manager.PostAsJsonAsync("/api/hall/tables",
                new TableRequest(zone.Id, number, seats, TableShape.Rect, number * 10, 10, 12, 12), ApiFixture.Json)))).Id;
        return new Floor(manager, waiter, zone.Id, await Table(1, 2), await Table(2, 4), await Table(3, 6));
    }

    private sealed record TableRef(Guid Id, int Number, int Version);

    private static async Task<HttpResponseMessage> Ensure(Task<HttpResponseMessage> call)
    {
        var response = await call;
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return response;
    }

    private static ReservationRequest Booking(Guid table, DateOnly date, string time, int guests = 2, int minutes = 120, Guid? id = null) =>
        new(id ?? Guid.NewGuid(), table, "Мария", "+7 900 000-00-00", guests, date, TimeOnly.Parse(time), minutes);

    private static Task<HttpResponseMessage> Book(HttpClient client, ReservationRequest request) =>
        client.PostAsJsonAsync("/api/hall/reservations", request, ApiFixture.Json);

    private static async Task<HallView> View(HttpClient client, string? query = null) =>
        (await client.GetFromJsonAsync<HallView>("/api/hall" + query, ApiFixture.Json))!;

    private static TableView Table(HallView view, Guid id) => view.Tables.Single(t => t.Id == id);

    /// <summary>Местное время ресторана через minutes минут, округлённое вверх до 5 минут: дата и «ЧЧ:ММ».</summary>
    private (DateOnly Date, string Time) In(int minutes)
    {
        var t = LocalNow.AddMinutes(minutes);
        t = t.AddTicks(TimeSpan.FromMinutes(5).Ticks - t.Ticks % TimeSpan.FromMinutes(5).Ticks);
        return (DateOnly.FromDateTime(t.DateTime), t.ToString("HH:mm"));
    }

    [Fact]
    public async Task Бронь_на_четверых_предлагается_только_за_столы_от_четырёх_мест()
    {
        var floor = await FloorAsync();

        var options = await floor.Waiter.GetFromJsonAsync<List<TableOption>>($"/api/hall/options?date={Tomorrow:yyyy-MM-dd}&time=19:00&minutes=120&guests=4", ApiFixture.Json);

        Assert.Equal([floor.Four, floor.Six], options!.Where(o => o.Fits).Select(o => o.TableId));
        Assert.Equal("Мало мест: 2 из 4", options!.Single(o => o.TableId == floor.Two).Reason);
        // Сервер проверяет вместимость сам, не полагаясь на форму.
        var crowded = await Book(floor.Waiter, Booking(floor.Two, Tomorrow, "19:00", guests: 4));
        Assert.Equal(HttpStatusCode.BadRequest, crowded.StatusCode);
    }

    [Fact]
    public async Task Пересечение_и_нарушение_перерыва_отклоняются_соседняя_бронь_с_перерывом_принимается()
    {
        var floor = await FloorAsync();
        Assert.Equal(HttpStatusCode.Created, (await Book(floor.Waiter, Booking(floor.Four, Tomorrow, "18:00"))).StatusCode);

        Assert.Equal(HttpStatusCode.Conflict, (await Book(floor.Waiter, Booking(floor.Four, Tomorrow, "19:00"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Book(floor.Waiter, Booking(floor.Four, Tomorrow, "20:00"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Book(floor.Waiter, Booking(floor.Four, Tomorrow, "16:00", minutes: 110))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Book(floor.Waiter, Booking(floor.Four, Tomorrow, "20:15"))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Book(floor.Waiter, Booking(floor.Four, Tomorrow, "16:00", minutes: 105))).StatusCode);
        // Другой стол на то же время свободен.
        Assert.Equal(HttpStatusCode.Created, (await Book(floor.Waiter, Booking(floor.Six, Tomorrow, "19:00"))).StatusCode);
    }

    [Fact]
    public async Task Одновременные_брони_и_посадки_за_один_стол_не_создают_двойников()
    {
        var floor = await FloorAsync();

        var bookings = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Book(floor.Waiter, Booking(floor.Four, Tomorrow, "19:00"))));
        Assert.Equal(1, bookings.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(7, bookings.Count(r => r.StatusCode == HttpStatusCode.Conflict));

        var seats = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            floor.Waiter.PostAsJsonAsync($"/api/hall/tables/{floor.Six}/seat", new SeatRequest(Guid.NewGuid(), 3, 60), ApiFixture.Json)));
        Assert.Equal(1, seats.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(7, seats.Count(r => r.StatusCode == HttpStatusCode.Conflict));

        // Двойное нажатие: тот же идентификатор — та же бронь и тот же визит.
        var request = Booking(floor.Two, Tomorrow, "12:00");
        var twice = await Task.WhenAll(Book(floor.Waiter, request), Book(floor.Waiter, request));
        Assert.All(twice, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        var list = await floor.Waiter.GetFromJsonAsync<List<ReservationView>>($"/api/hall/reservations?date={Tomorrow:yyyy-MM-dd}", ApiFixture.Json);
        Assert.Single(list!, r => r.TableId == floor.Two);

        var visit = new SeatRequest(Guid.NewGuid(), 2, 30);
        var again = await Task.WhenAll(
            floor.Waiter.PostAsJsonAsync($"/api/hall/tables/{floor.Two}/seat", visit, ApiFixture.Json),
            floor.Waiter.PostAsJsonAsync($"/api/hall/tables/{floor.Two}/seat", visit, ApiFixture.Json));
        Assert.All(again, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(visit.Id, Table(await View(floor.Waiter), floor.Two).Visit!.Id);
    }

    [Fact]
    public async Task Посадка_по_брони_завершение_уборка_готовность()
    {
        var floor = await FloorAsync();
        var soon = In(20);   // ближе 30 минут: стол уже показывается как «бронь»
        var later = In(240);
        var booking = await Read<ReservationView>(await Ensure(Book(floor.Waiter, Booking(floor.Four, soon.Date, soon.Time, guests: 3, minutes: 90))));
        var next = await Read<ReservationView>(await Ensure(Book(floor.Waiter, Booking(floor.Four, later.Date, later.Time, guests: 2))));
        Assert.Equal(TableState.Reserved, Table(await View(floor.Waiter), floor.Four).State);

        // Гости пришли раньше: та же бронь переходит в «за столом», стол занят.
        var seated = await floor.Waiter.PostAsync($"/api/hall/reservations/{booking.Id}/seat", null);
        Assert.Equal(HttpStatusCode.OK, seated.StatusCode);
        var visit = await Read<VisitView>(seated);
        Assert.Equal(booking.Id, visit.ReservationId);
        var occupied = Table(await View(floor.Waiter), floor.Four);
        Assert.Equal(TableState.Occupied, occupied.State);
        Assert.Equal(ReservationStatus.Seated, (await View(floor.Waiter)).Reservations.Single(r => r.Id == booking.Id).Status);

        fx.Time.Advance(TimeSpan.FromMinutes(100));
        Assert.Equal(HttpStatusCode.OK, (await floor.Waiter.PostAsync($"/api/hall/visits/{visit.Id}/finish", null)).StatusCode);
        var cleaning = Table(await View(floor.Waiter), floor.Four);
        Assert.Equal(TableState.Cleaning, cleaning.State);
        // Убранный стол сам по таймеру не освобождается.
        fx.Time.Advance(TimeSpan.FromMinutes(20));
        Assert.Equal(TableState.Cleaning, Table(await View(floor.Waiter), floor.Four).State);
        // Пока стол на уборке, посадить за него нельзя.
        Assert.Equal(HttpStatusCode.Conflict, (await floor.Waiter.PostAsJsonAsync($"/api/hall/tables/{floor.Four}/seat", new SeatRequest(null, 2, 30), ApiFixture.Json)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await floor.Waiter.PostAsync($"/api/hall/tables/{floor.Four}/ready", null)).StatusCode);
        var free = Table(await View(floor.Waiter), floor.Four);
        Assert.Equal(TableState.Free, free.State);
        // Свободен с учётом следующей брони: до её начала минус перерыв.
        Assert.Equal(next.StartsAt.AddMinutes(-15), free.FreeUntil);
        Assert.Equal(next.Id, free.Next!.Id);
        var done = (await floor.Waiter.GetFromJsonAsync<List<ReservationView>>($"/api/hall/reservations?date={soon.Date:yyyy-MM-dd}", ApiFixture.Json))!.Single(r => r.Id == booking.Id);
        Assert.Equal(ReservationStatus.Completed, done.Status);
    }

    [Fact]
    public async Task Задержавшиеся_гости_держат_стол_и_показывают_риск_для_следующей_брони()
    {
        var floor = await FloorAsync();
        var seat = await floor.Waiter.PostAsJsonAsync($"/api/hall/tables/{floor.Four}/seat", new SeatRequest(null, 2, 60), ApiFixture.Json);
        var visit = await Read<VisitView>(seat);
        var at = In(80);   // бронь через 80–85 минут: визит на час с перерывом в неё укладывается
        await Ensure(Book(floor.Waiter, Booking(floor.Four, at.Date, at.Time, guests: 4)));

        fx.Time.Advance(TimeSpan.FromMinutes(72));   // ожидаемое время визита прошло 12 минут назад
        var table = Table(await View(floor.Waiter), floor.Four);
        Assert.Equal(TableState.Occupied, table.State);
        Assert.Contains("дольше", table.Label);
        Assert.Contains("под угрозой", table.Warning);

        var extended = await floor.Waiter.PostAsJsonAsync($"/api/hall/visits/{visit.Id}/extend", new ExtendRequest(30), ApiFixture.Json);
        Assert.Equal(HttpStatusCode.OK, extended.StatusCode);
        var after = Table(await View(floor.Waiter), floor.Four);
        Assert.Equal(TableState.Occupied, after.State);
        Assert.Contains("под угрозой", after.Warning);
    }

    [Fact]
    public async Task Посадка_без_брони_учитывает_следующую_бронь()
    {
        var floor = await FloorAsync();
        var at = In(90);
        await Ensure(Book(floor.Waiter, Booking(floor.Four, at.Date, at.Time)));

        var refused = await floor.Waiter.PostAsJsonAsync($"/api/hall/tables/{floor.Four}/seat", new SeatRequest(null, 2, 120), ApiFixture.Json);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var problem = await refused.Content.ReadFromJsonAsync<Dictionary<string, System.Text.Json.JsonElement>>();
        var minutes = problem!["availableMinutes"].GetInt32();
        Assert.InRange(minutes, 75, 80);   // до брони 90–95 минут минус перерыв 15

        Assert.Equal(HttpStatusCode.OK, (await floor.Waiter.PostAsJsonAsync($"/api/hall/tables/{floor.Four}/seat", new SeatRequest(null, 2, minutes), ApiFixture.Json)).StatusCode);
    }

    [Fact]
    public async Task Отмена_освобождает_интервал_и_пишется_в_журнал_неявка_только_после_ожидания()
    {
        var floor = await FloorAsync();
        var first = await Read<ReservationView>(await Ensure(Book(floor.Waiter, Booking(floor.Four, Tomorrow, "19:00"))));
        Assert.Equal(HttpStatusCode.OK, (await floor.Waiter.PostAsync($"/api/hall/reservations/{first.Id}/cancel", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Book(floor.Waiter, Booking(floor.Four, Tomorrow, "19:00"))).StatusCode);
        var journal = await floor.Manager.GetFromJsonAsync<List<HallEventRow>>("/api/hall/journal", ApiFixture.Json);
        Assert.Contains(journal!, e => e.EntityId == first.Id && e.Action == "cancelled" && e.Text.Contains("Мария"));
        // Отменённую бронь не посадить и не отменить повторно в другой статус.
        Assert.Equal(HttpStatusCode.Conflict, (await floor.Waiter.PostAsync($"/api/hall/reservations/{first.Id}/seat", null)).StatusCode);

        var soon = In(10);
        var late = await Read<ReservationView>(await Ensure(Book(floor.Waiter, Booking(floor.Six, soon.Date, soon.Time))));
        Assert.Equal(HttpStatusCode.Conflict, (await floor.Waiter.PostAsync($"/api/hall/reservations/{late.Id}/no-show", null)).StatusCode);
        fx.Time.Advance(late.StartsAt - Now + TimeSpan.FromMinutes(14));
        Assert.Equal(HttpStatusCode.Conflict, (await floor.Waiter.PostAsync($"/api/hall/reservations/{late.Id}/no-show", null)).StatusCode);
        fx.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.Contains("опаздывает", Table(await View(floor.Waiter), floor.Six).Warning ?? "", StringComparison.Ordinal);
        var noShow = await floor.Waiter.PostAsync($"/api/hall/reservations/{late.Id}/no-show", null);
        Assert.Equal(HttpStatusCode.OK, noShow.StatusCode);
        Assert.Equal(ReservationStatus.NoShow, (await Read<ReservationView>(noShow)).Status);
    }

    [Fact]
    public async Task Перенос_при_конфликте_сохраняет_исходную_бронь()
    {
        var floor = await FloorAsync();
        await Ensure(Book(floor.Waiter, Booking(floor.Four, Tomorrow, "18:00")));
        var moving = await Read<ReservationView>(await Ensure(Book(floor.Waiter, Booking(floor.Six, Tomorrow, "18:00"))));

        var clash = await floor.Waiter.PutAsJsonAsync($"/api/hall/reservations/{moving.Id}",
            Booking(floor.Four, Tomorrow, "18:30") with { Version = moving.Version }, ApiFixture.Json);
        Assert.Equal(HttpStatusCode.Conflict, clash.StatusCode);
        var kept = (await floor.Waiter.GetFromJsonAsync<List<ReservationView>>($"/api/hall/reservations?date={Tomorrow:yyyy-MM-dd}", ApiFixture.Json))!.Single(r => r.Id == moving.Id);
        Assert.Equal((floor.Six, moving.StartsAt, moving.Version), (kept.TableId, kept.StartsAt, kept.Version));

        var moved = await floor.Waiter.PutAsJsonAsync($"/api/hall/reservations/{moving.Id}",
            Booking(floor.Four, Tomorrow, "20:15") with { Version = moving.Version }, ApiFixture.Json);
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        var result = await Read<ReservationView>(moved);
        Assert.Equal((floor.Four, moving.Version + 1), (result.TableId, result.Version));
        // Прежнее время на старом столе освободилось.
        Assert.Equal(HttpStatusCode.Created, (await Book(floor.Waiter, Booking(floor.Six, Tomorrow, "18:00"))).StatusCode);
        // Устаревшая форма (старая версия) не затирает перенос.
        var stale = await floor.Waiter.PutAsJsonAsync($"/api/hall/reservations/{moving.Id}",
            Booking(floor.Two, Tomorrow, "12:00") with { Version = moving.Version }, ApiFixture.Json);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
    }

    [Fact]
    public async Task Официант_не_меняет_схему_и_чужой_ресторан_недоступен()
    {
        var floor = await FloorAsync();
        var booking = await Read<ReservationView>(await Ensure(Book(floor.Waiter, Booking(floor.Four, Tomorrow, "19:00"))));

        Assert.Equal(HttpStatusCode.Forbidden, (await floor.Waiter.PostAsJsonAsync("/api/hall/zones", new ZoneRequest("Подвал"), ApiFixture.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await floor.Waiter.PutAsJsonAsync($"/api/hall/tables/{floor.Two}",
            new TableRequest(floor.Zone, 1, 8, TableShape.Rect, 0, 0, 10, 10, Version: 0), ApiFixture.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await floor.Waiter.PostAsJsonAsync($"/api/hall/tables/{floor.Two}/block", new BlockRequest(null, null, null, null, "ремонт"), ApiFixture.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await floor.Waiter.PutAsJsonAsync("/api/hall/settings", new HallSettingsDto(60, 0, 0, 0), ApiFixture.Json)).StatusCode);
        Assert.False((await View(floor.Waiter)).CanManage);
        Assert.True((await View(floor.Manager)).CanManage);
        // Кладовщику зал закрыт целиком.
        var storekeeper = await fx.SignInAsync("storekeeper");
        Assert.Equal(HttpStatusCode.Forbidden, (await storekeeper.GetAsync("/api/hall")).StatusCode);

        var other = await FloorAsync();
        Assert.DoesNotContain((await View(other.Manager)).Tables, t => t.Id == floor.Four);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Manager.PostAsync($"/api/hall/reservations/{booking.Id}/cancel", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Waiter.PostAsJsonAsync($"/api/hall/tables/{floor.Four}/seat", new SeatRequest(null, 2, 30), ApiFixture.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Book(other.Waiter, Booking(floor.Four, Tomorrow, "12:00"))).StatusCode);
        Assert.DoesNotContain((await other.Manager.GetFromJsonAsync<List<HallEventRow>>("/api/hall/journal", ApiFixture.Json))!, e => e.EntityId == booking.Id);
    }

    [Fact]
    public async Task Время_ресторана_и_бронь_через_полночь()
    {
        var floor = await FloorAsync();
        var booking = await Read<ReservationView>(await Ensure(Book(floor.Waiter, Booking(floor.Four, Tomorrow, "23:00", minutes: 150))));

        // 23:00 по Москве — 20:00 UTC; конец — уже следующей календарной датой.
        Assert.Equal(new DateTimeOffset(Tomorrow.ToDateTime(new TimeOnly(20, 0)), TimeSpan.Zero), booking.StartsAt);
        Assert.Equal(booking.StartsAt.AddMinutes(150), booking.EndsAt);
        var after = Tomorrow.AddDays(1);
        // В 00:30 следующего дня стол забронирован, а утренняя бронь того дня с перерывом после 01:30 возможна.
        var night = await View(floor.Waiter, $"?date={after:yyyy-MM-dd}&time=00:30");
        Assert.False(night.Live);
        Assert.Equal(TableState.Reserved, Table(night, floor.Four).State);
        Assert.Contains(night.Reservations, r => r.Id == booking.Id);
        Assert.Equal(HttpStatusCode.Conflict, (await Book(floor.Waiter, Booking(floor.Four, after, "01:30"))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Book(floor.Waiter, Booking(floor.Four, after, "01:45"))).StatusCode);
        Assert.Equal(180, (await View(floor.Waiter)).UtcOffsetMinutes);
    }

    [Fact]
    public async Task Закрытие_стола_с_бронями_требует_решения_и_не_ставится_поверх_визита()
    {
        var floor = await FloorAsync();
        var booking = await Read<ReservationView>(await Ensure(Book(floor.Waiter, Booking(floor.Four, Tomorrow, "19:00"))));

        var refused = await floor.Manager.PostAsJsonAsync($"/api/hall/tables/{floor.Four}/block", new BlockRequest(null, null, null, null, "Ремонт"), ApiFixture.Json);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var problem = await refused.Content.ReadFromJsonAsync<Dictionary<string, System.Text.Json.JsonElement>>();
        Assert.Contains("Мария", problem!["detail"].GetString());
        Assert.Equal(booking.Id, problem["reservations"][0].GetProperty("id").GetGuid());
        Assert.Equal(ReservationStatus.Confirmed, (await View(floor.Manager, $"?date={Tomorrow:yyyy-MM-dd}&time=19:00")).Reservations.Single(r => r.Id == booking.Id).Status);

        var blocked = await floor.Manager.PostAsJsonAsync($"/api/hall/tables/{floor.Four}/block", new BlockRequest(null, null, null, null, "Ремонт", CancelReservations: true), ApiFixture.Json);
        Assert.Equal(HttpStatusCode.OK, blocked.StatusCode);
        var view = await View(floor.Manager);
        Assert.Equal(TableState.Blocked, Table(view, floor.Four).State);
        Assert.Equal(HttpStatusCode.Conflict, (await Book(floor.Waiter, Booking(floor.Four, Tomorrow, "12:00"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await floor.Waiter.PostAsJsonAsync($"/api/hall/tables/{floor.Four}/seat", new SeatRequest(null, 2, 30), ApiFixture.Json)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await floor.Manager.PostAsync($"/api/hall/blocks/{(await Read<BlockView>(blocked)).Id}/lift", null)).StatusCode);
        Assert.Equal(TableState.Free, Table(await View(floor.Manager), floor.Four).State);

        await Ensure(floor.Waiter.PostAsJsonAsync($"/api/hall/tables/{floor.Six}/seat", new SeatRequest(null, 2, 30), ApiFixture.Json));
        Assert.Equal(HttpStatusCode.Conflict, (await floor.Manager.PostAsJsonAsync($"/api/hall/tables/{floor.Six}/block", new BlockRequest(null, null, null, null, "Ремонт"), ApiFixture.Json)).StatusCode);
    }

    [Fact]
    public async Task Нельзя_уменьшить_стол_ниже_будущей_брони_и_номер_стола_годится_для_заказа()
    {
        var floor = await FloorAsync();
        await Ensure(Book(floor.Waiter, Booking(floor.Six, Tomorrow, "19:00", guests: 5)));
        var view = await View(floor.Manager);
        var six = Table(view, floor.Six);

        var shrink = await floor.Manager.PutAsJsonAsync($"/api/hall/tables/{floor.Six}",
            new TableRequest(floor.Zone, six.Number, 4, six.Shape, six.X, six.Y, six.W, six.H, Version: six.Version), ApiFixture.Json);
        Assert.Equal(HttpStatusCode.Conflict, shrink.StatusCode);
        var archive = await floor.Manager.PutAsJsonAsync($"/api/hall/tables/{floor.Six}",
            new TableRequest(floor.Zone, six.Number, 6, six.Shape, six.X, six.Y, six.W, six.H, Archived: true, Version: six.Version), ApiFixture.Json);
        Assert.Equal(HttpStatusCode.Conflict, archive.StatusCode);

        // Стол с большим номером добавляет место «Стол 40» в список мест для заказа.
        await Ensure(floor.Manager.PostAsJsonAsync("/api/hall/tables", new TableRequest(floor.Zone, 40, 2, TableShape.Round, 50, 50, 10, 10), ApiFixture.Json));
        var me = await floor.Waiter.GetFromJsonAsync<MeResponse>("/api/me", ApiFixture.Json);
        Assert.Contains("Стол 40", me!.Places);
        // Повторный номер у действующего стола не допускается.
        Assert.Equal(HttpStatusCode.BadRequest, (await floor.Manager.PostAsJsonAsync("/api/hall/tables", new TableRequest(floor.Zone, 40, 2, TableShape.Round, 0, 0, 10, 10), ApiFixture.Json)).StatusCode);
    }
}
