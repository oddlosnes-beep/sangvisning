package no.sangvisning.app;

import android.app.Activity;
import android.content.Intent;
import android.content.SharedPreferences;
import android.net.Uri;
import android.os.Bundle;
import android.view.Gravity;
import android.view.MotionEvent;
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
        View hot = new View(this);
        int hs = (int) (110 * getResources().getDisplayMetrics().density);
        hot.setOnTouchListener((v, e) -> {
            if (e.getAction() == MotionEvent.ACTION_DOWN) {
                long n = System.currentTimeMillis();
                taps = (n - lastTap < 1500) ? taps + 1 : 1;
                lastTap = n;
                if (taps >= 3) { taps = 0; sok(false, false); }
            }
            return false;
        });
        root.addView(hot, new FrameLayout.LayoutParams(hs, hs, Gravity.TOP | Gravity.START));
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
                if (r.isForMainFrame()) {
                    if (!auto) { auto = true; sok(true, true); } else visFeil();
                }
            }

            @Override
            public void onPageFinished(WebView v, String url) {
                if (url != null && url.startsWith("http://")) auto = false;
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
            husk(d.getAuthority());
        }
        start();
    }

    void start() {
        String a = sp.getString("adr", "");
        if (a.isEmpty()) { auto = true; sok(true, false); return; }
        panel.setVisibility(View.GONE);
        immersive();
        wv.loadUrl("http://" + a + "/menu.html");
    }

    ScrollView panel;
    TextView pTitle, pMsg;
    EditText pAdr;
    Button pOk, pRetry, pBack;
    LinearLayout pList;
    int taps = 0; long lastTap = 0;
    java.util.Map<String, String> funnet = new java.util.LinkedHashMap<>();
    boolean auto = false;

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
        pRetry.setOnClickListener(v -> { auto = false; start(); });
        pBack = new Button(this); pBack.setText("Tilbake"); pBack.setTextSize(18);
        pBack.setOnClickListener(v -> { auto = false; start(); });
        pList = new LinearLayout(this); pList.setOrientation(LinearLayout.VERTICAL);
        l.addView(pTitle); l.addView(pMsg); l.addView(pAdr); l.addView(pOk); l.addView(pRetry); l.addView(pBack); l.addView(pList);
        panel.addView(l);
        root.addView(panel, new FrameLayout.LayoutParams(-1, -1));
        panel.setVisibility(View.GONE);
    }

    void visPanel(boolean feil) {
        String a = sp.getString("adr", "");
        pTitle.setText(feil ? "Får ikke kontakt" : "Sangvisning");
        pMsg.setText(feil ? "Fant ikke Sangvisning på " + a + ".\nSjekk at programmet kjører på PC-en og at nettbrettet er på samme wifi. Du kan også rette adressen under."
                : "Skriv inn adressen til PC-en. Den står i Sangvisning på PC-en under Mer ▸ Mobil og nettbrett. Eller skann QR-koden der. Du kommer hit igjen ved å trykke tre ganger raskt i øvre venstre hjørne.");
        pAdr.setText(a);
        pRetry.setVisibility(feil ? View.VISIBLE : View.GONE);
        pBack.setVisibility(!feil && !a.isEmpty() ? View.VISIBLE : View.GONE);
        pList.removeAllViews();
        if (!funnet.isEmpty()) {
            TextView t0 = new TextView(this); t0.setText("Funnet på nettverket nå:"); t0.setTextColor(0xFFC8C8D0); t0.setTextSize(16); t0.setPadding(0, 40, 0, 0);
            pList.addView(t0);
            for (java.util.Map.Entry<String, String> e : funnet.entrySet()) {
                final String xa = e.getKey();
                Button b = new Button(this); b.setText((e.getValue().isEmpty() ? "" : e.getValue() + "  ") + xa); b.setAllCaps(false); b.setTextSize(18);
                b.setOnClickListener(v -> lagre(xa));
                pList.addView(b);
            }
        }
        String lst = sp.getString("liste", "");
        if (!lst.isEmpty()) {
            TextView t = new TextView(this); t.setText("Tidligere brukte PC-er:"); t.setTextColor(0xFFC8C8D0); t.setTextSize(16); t.setPadding(0, 40, 0, 0);
            pList.addView(t);
            for (String x : lst.split("\\|")) {
                if (x.isEmpty()) continue;
                final String xa = x;
                Button b = new Button(this); b.setText(xa); b.setAllCaps(false); b.setTextSize(18);
                b.setOnClickListener(v -> lagre(xa));
                pList.addView(b);
            }
        }
        panel.setVisibility(View.VISIBLE);
        getWindow().getDecorView().setSystemUiVisibility(0);
    }

    void sok(boolean autoKoble, boolean feil) {
        visPanel(feil);
        pMsg.setText("Søker etter Sangvisning på nettverket …");
        new Thread(() -> {
            java.util.Map<String, String> res = new java.util.LinkedHashMap<>();
            try (java.net.DatagramSocket ds = new java.net.DatagramSocket()) {
                ds.setBroadcast(true);
                ds.setSoTimeout(500);
                byte[] q = "SANGVISNING?".getBytes();
                for (int i = 0; i < 3; i++)
                    ds.send(new java.net.DatagramPacket(q, q.length, java.net.InetAddress.getByName("255.255.255.255"), 45678));
                long end = System.currentTimeMillis() + 2500;
                byte[] buf = new byte[256];
                while (System.currentTimeMillis() < end) {
                    try {
                        java.net.DatagramPacket pk = new java.net.DatagramPacket(buf, buf.length);
                        ds.receive(pk);
                        String[] x = new String(pk.getData(), 0, pk.getLength()).split(":", 3);
                        if (x.length >= 2 && x[0].equals("SANGVISNING"))
                            res.put(pk.getAddress().getHostAddress() + ":" + x[1], x.length > 2 ? x[2] : "");
                    } catch (java.net.SocketTimeoutException e) { }
                }
            } catch (Exception e) { }
            runOnUiThread(() -> {
                funnet = res;
                if (autoKoble && res.size() == 1) { husk(res.keySet().iterator().next()); start(); }
                else visPanel(feil);
            });
        }).start();
    }

    void visOppsett() { visPanel(false); }
    void visFeil() { visPanel(true); }

    void husk(String a) {
        StringBuilder sb = new StringBuilder(a);
        int n = 1;
        for (String x : sp.getString("liste", "").split("\\|")) {
            if (x.isEmpty() || x.equals(a) || n >= 6) continue;
            sb.append('|').append(x); n++;
        }
        sp.edit().putString("adr", a).putString("liste", sb.toString()).apply();
    }

    void lagre(String adr) {
        auto = false;
        String a = adr == null ? "" : adr.trim().replace("http://", "").replace("https://", "");
        int sl = a.indexOf('/');
        if (sl >= 0) a = a.substring(0, sl);
        if (a.isEmpty()) { Toast.makeText(this, "Skriv inn adressen først", Toast.LENGTH_SHORT).show(); return; }
        if (!a.contains(":")) a = a + ":5000";
        husk(a);
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
