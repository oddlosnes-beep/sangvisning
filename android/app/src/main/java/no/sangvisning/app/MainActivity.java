package no.sangvisning.app;

import android.app.Activity;
import android.content.Intent;
import android.content.SharedPreferences;
import android.net.Uri;
import android.os.Bundle;
import android.view.View;
import android.view.WindowManager;
import android.webkit.JavascriptInterface;
import android.webkit.WebChromeClient;
import android.webkit.WebResourceError;
import android.webkit.WebResourceRequest;
import android.webkit.WebSettings;
import android.webkit.WebView;
import android.webkit.WebViewClient;

public class MainActivity extends Activity {
    WebView wv;
    SharedPreferences sp;

    @Override
    protected void onCreate(Bundle b) {
        super.onCreate(b);
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        sp = getSharedPreferences("sangvisning", MODE_PRIVATE);
        wv = new WebView(this);
        wv.setBackgroundColor(0xFF000000);
        setContentView(wv);
        WebSettings s = wv.getSettings();
        s.setJavaScriptEnabled(true);
        s.setDomStorageEnabled(true);
        s.setMediaPlaybackRequiresUserGesture(false);
        s.setCacheMode(WebSettings.LOAD_NO_CACHE);
        wv.addJavascriptInterface(new Bridge(), "Android");
        wv.setWebChromeClient(new WebChromeClient());
        wv.setWebViewClient(new WebViewClient() {
            @Override
            public void onReceivedError(WebView v, WebResourceRequest r, WebResourceError e) {
                if (r.isForMainFrame()) visFeil();
            }
        });
        lesIntent(getIntent());
    }

    @Override
    protected void onNewIntent(Intent i) {
        super.onNewIntent(i);
        lesIntent(i);
    }

    void lesIntent(Intent i) {
        Uri d = i == null ? null : i.getData();
        if (d != null && "sangvisning".equals(d.getScheme()) && d.getAuthority() != null && !d.getAuthority().isEmpty()) {
            sp.edit().putString("adr", d.getAuthority()).apply();
        }
        start();
    }

    void start() {
        String a = sp.getString("adr", "");
        if (a.isEmpty()) visOppsett();
        else wv.loadUrl("http://" + a + "/menu.html");
    }

    static final String CSS = "<meta name=viewport content='width=device-width,initial-scale=1'><style>body{background:#15161a;color:#ececf1;font:20px system-ui;padding:8vw;max-width:640px;margin:auto}input,button{font:inherit;padding:14px;border-radius:12px;border:1px solid #555;width:100%;box-sizing:border-box;margin-top:12px}button{background:#2f6fed;color:#fff;border:0}.g{background:#33343c}small{color:#9a9aa6}</style>";

    void visOppsett() {
        String h = "<html><head>" + CSS + "</head><body><h1>Sangvisning</h1><p>Skriv inn adressen til PC-en. Den står i Sangvisning på PC-en under Mer ▸ Mobil og nettbrett.</p>"
                + "<input id=a placeholder='192.168.10.125:5000' inputmode=url autocapitalize=off>"
                + "<button onclick=\"Android.lagre(document.getElementById('a').value)\">Koble til</button>"
                + "<p><small>Tips: skanner du QR-koden på PC-en, settes adressen automatisk.</small></p></body></html>";
        wv.loadDataWithBaseURL(null, h, "text/html", "utf-8", null);
    }

    void visFeil() {
        String h = "<html><head>" + CSS + "</head><body><h1>Får ikke kontakt</h1><p>Fant ikke Sangvisning på " + sp.getString("adr", "") + ".</p>"
                + "<p><small>Sjekk at Sangvisning kjører på PC-en og at nettbrettet er på samme wifi.</small></p>"
                + "<button onclick='Android.prov()'>Prøv igjen</button><button class=g onclick='Android.endre()'>Endre adresse</button></body></html>";
        wv.loadDataWithBaseURL(null, h, "text/html", "utf-8", null);
    }

    class Bridge {
        @JavascriptInterface
        public void lagre(String adr) {
            String a = adr == null ? "" : adr.trim().replace("http://", "").replace("https://", "");
            int sl = a.indexOf('/');
            if (sl >= 0) a = a.substring(0, sl);
            if (a.isEmpty()) return;
            if (!a.contains(":")) a = a + ":5000";
            final String fa = a;
            runOnUiThread(() -> { sp.edit().putString("adr", fa).apply(); start(); });
        }

        @JavascriptInterface
        public void prov() { runOnUiThread(() -> start()); }

        @JavascriptInterface
        public void endre() { runOnUiThread(() -> visOppsett()); }
    }

    @Override
    public void onBackPressed() {
        if (wv.canGoBack()) wv.goBack();
        else super.onBackPressed();
    }

    @Override
    protected void onResume() {
        super.onResume();
        wv.onResume();
    }

    @Override
    public void onWindowFocusChanged(boolean f) {
        super.onWindowFocusChanged(f);
        if (f) getWindow().getDecorView().setSystemUiVisibility(
                View.SYSTEM_UI_FLAG_LAYOUT_STABLE | View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION
                | View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN | View.SYSTEM_UI_FLAG_HIDE_NAVIGATION
                | View.SYSTEM_UI_FLAG_FULLSCREEN | View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY);
    }
}
