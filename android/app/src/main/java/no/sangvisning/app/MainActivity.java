package no.sangvisning.app;

import android.app.Activity;
import android.content.Intent;
import android.content.SharedPreferences;
import android.net.Uri;
import android.os.Bundle;
import android.view.View;
import android.view.WindowManager;
import android.widget.*;
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
        FrameLayout root = new FrameLayout(this);
        root.addView(wv, new FrameLayout.LayoutParams(-1, -1));
        byggPanel(root);
        setContentView(root);
        WebSettings s = wv.getSettings();
        s.setJavaScriptEnabled(true);
        s.setDomStorageEnabled(true);
        s.setMediaPlaybackRequiresUserGesture(false);
        s.setCacheMode(WebSettings.LOAD_NO_CACHE);
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
        if (a.isEmpty()) { visOppsett(); return; }
        panel.setVisibility(View.GONE);
        immersive();
        wv.loadUrl("http://" + a + "/menu.html");
    }

    ScrollView panel;
    TextView pTitle, pMsg;
    EditText pAdr;
    Button pOk, pRetry;

    void byggPanel(FrameLayout root) {
        panel = new ScrollView(this);
        panel.setBackgroundColor(0xFF15161A);
        LinearLayout l = new LinearLayout(this);
        l.setOrientation(LinearLayout.VERTICAL);
        int p = (int) (24 * getResources().getDisplayMetrics().density);
        l.setPadding(p, p * 2, p, p);
        pTitle = new TextView(this); pTitle.setTextColor(0xFFECECF1); pTitle.setTextSize(26);
        pMsg = new TextView(this); pMsg.setTextColor(0xFFC8C8D0); pMsg.setTextSize(18); pMsg.setPadding(0, p / 2, 0, p / 2);
        pAdr = new EditText(this); pAdr.setHint("192.168.10.125:5000"); pAdr.setTextColor(0xFFFFFFFF); pAdr.setHintTextColor(0xFF777777);
        pAdr.setTextSize(20); pAdr.setSingleLine(true); pAdr.setInputType(android.text.InputType.TYPE_TEXT_VARIATION_URI);
        pOk = new Button(this); pOk.setText("Koble til"); pOk.setTextSize(18);
        pRetry = new Button(this); pRetry.setText("Prøv igjen"); pRetry.setTextSize(18);
        pOk.setOnClickListener(v -> lagre(pAdr.getText().toString()));
        pRetry.setOnClickListener(v -> start());
        l.addView(pTitle); l.addView(pMsg); l.addView(pAdr); l.addView(pOk); l.addView(pRetry);
        panel.addView(l);
        root.addView(panel, new FrameLayout.LayoutParams(-1, -1));
        panel.setVisibility(View.GONE);
    }

    void visPanel(boolean feil) {
        String a = sp.getString("adr", "");
        pTitle.setText(feil ? "Får ikke kontakt" : "Sangvisning");
        pMsg.setText(feil ? "Fant ikke Sangvisning på " + a + ".\nSjekk at programmet kjører på PC-en og at nettbrettet er på samme wifi. Du kan også rette adressen under."
                : "Skriv inn adressen til PC-en. Den står i Sangvisning på PC-en under Mer ▸ Mobil og nettbrett. Eller skann QR-koden der.");
        pAdr.setText(a);
        pRetry.setVisibility(feil ? View.VISIBLE : View.GONE);
        panel.setVisibility(View.VISIBLE);
        getWindow().getDecorView().setSystemUiVisibility(0);
    }

    void visOppsett() { visPanel(false); }
    void visFeil() { visPanel(true); }

    void lagre(String adr) {
        String a = adr == null ? "" : adr.trim().replace("http://", "").replace("https://", "");
        int sl = a.indexOf('/');
        if (sl >= 0) a = a.substring(0, sl);
        if (a.isEmpty()) { Toast.makeText(this, "Skriv inn adressen først", Toast.LENGTH_SHORT).show(); return; }
        if (!a.contains(":")) a = a + ":5000";
        sp.edit().putString("adr", a).apply();
        start();
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
        if (f && panel.getVisibility() != View.VISIBLE) immersive();
    }

    void immersive() {
        getWindow().getDecorView().setSystemUiVisibility(
                View.SYSTEM_UI_FLAG_LAYOUT_STABLE | View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION
                | View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN | View.SYSTEM_UI_FLAG_HIDE_NAVIGATION
                | View.SYSTEM_UI_FLAG_FULLSCREEN | View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY);
    }
}
