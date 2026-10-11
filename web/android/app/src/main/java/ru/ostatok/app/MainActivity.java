package ru.ostatok.app;

import android.graphics.Color;
import android.os.Build;
import android.os.Bundle;
import android.view.View;
import android.webkit.WebView;
import androidx.core.splashscreen.SplashScreen;
import com.getcapacitor.BridgeActivity;
import com.getcapacitor.WebViewListener;

public class MainActivity extends BridgeActivity {

    private boolean pageShown;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        // Заставка остаётся, пока не нарисована первая страница: между ними нет пустого белого экрана.
        SplashScreen.installSplashScreen(this).setKeepOnScreenCondition(() -> !pageShown);
        super.onCreate(savedInstanceState);
        if (bridge == null) {
            pageShown = true;
            return;
        }
        WebView webView = bridge.getWebView();
        bridge.addWebViewListener(
            new WebViewListener() {
                @Override
                public void onPageCommitVisible(WebView view, String url) {
                    pageShown = true;
                }
            }
        );
        // Если страница так и не открылась, заставка не должна висеть вечно.
        webView.postDelayed(
            () -> {
                pageShown = true;
                webView.invalidate();
            },
            3000
        );
        // Без свечения и растягивания при прокрутке за край страницы.
        webView.setOverScrollMode(View.OVER_SCROLL_NEVER);
        // До Android 15 панель навигации непрозрачная: красим её в цвет нижней панели кабинета.
        // На Android 8.0 кнопки навигации всегда светлые, поэтому там панель остаётся тёмной.
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O_MR1) getWindow().setNavigationBarColor(Color.WHITE);
    }
}
