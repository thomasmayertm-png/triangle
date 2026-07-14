using UnityEngine;
using System.IO;

// =============================================================================
//  POSITION-FIXING PLAYGROUND  —  Method 1: TRIANGULATION (bearing based)
// -----------------------------------------------------------------------------
//  Two known stations (A, B) each measure a BEARING (compass direction) to an
//  unknown target. The target must lie somewhere along each bearing line, so the
//  fix is where the two lines CROSS. We reconstruct it two equivalent ways and
//  show they agree:
//      (a) line-line intersection  (the geometry)
//      (b) the LAW OF SINES         (the algebra you'd do by hand)
//
//  Drag A, B or the true target with the mouse; every line and number updates
//  live. The NOISE slider perturbs the measured bearings so you can watch the
//  clean crossing point wander away from the truth — that error is what later
//  methods (least-squares) are built to tame.
//
//  PROGRAMMING NOTES (for someone new to Unity/C#):
//   * [RuntimeInitializeOnLoadMethod] is a Unity hook: the tagged static method
//     runs automatically when you press Play, BEFORE anything else. We use it to
//     build the camera + this component from nothing, so there is no scene to
//     wire up by hand.
//   * OnPostRender() is called by the camera right after it draws the frame; it
//     is where we do immediate-mode "GL" drawing (lines/points straight to the
//     screen). Only works on the Built-in Render Pipeline, which is why we keep
//     the project on it.
//   * OnGUI() is Unity's old "immediate mode" UI. It is ugly but zero-setup and
//     perfect for throwaway sliders, toggles and floating text labels.
// =============================================================================
public class TriangulationApp : MonoBehaviour
{
    // ---- Bootstrap ----------------------------------------------------------
    // Runs once when Play starts. Creates a camera and attaches this script.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        var go = new GameObject("PositionFixingApp");
        var cam = go.AddComponent<Camera>();
        cam.orthographic = true;                         // top-down 2D, no perspective
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.07f, 0.08f, 0.10f);
        cam.transform.position = new Vector3(0, 0, -10);
        go.tag = "MainCamera";
        go.AddComponent<TriangulationApp>();
    }

    // ---- Model state (everything is in "map units", think metres) -----------
    // pts[0] = Station A, pts[1] = Station B, pts[2] = TRUE target position.
    Vector2[] pts = { new Vector2(-6f, -4f), new Vector2(6f, -4f), new Vector2(1.5f, 5f) };

    float noiseDeg = 0f;                 // std-dev of bearing error, degrees
    float gA = 0.6f, gB = -0.9f;         // fixed unit-normal samples (so noise is
                                         // steady while you drag, not flickering)

    int dragIdx = -1;                    // which point is being dragged (-1 = none)

    // --- TEMP DIAGNOSTIC (instrument-first): prove whether the button fires ---
    int clicks = 0;                      // how many times the button handler ran
    string logPath => Path.Combine(Application.dataPath, "..", "debug_noise.log");
    void Log(string m) { try { File.AppendAllText(logPath, System.DateTime.Now.ToString("HH:mm:ss.fff") + "  " + m + "\n"); } catch { } }
    const float mapSpan = 26f;           // how many map units span the short screen axis
    float ppu;                           // pixels per map unit (recomputed each frame)

    Material glMat;                      // material used for GL immediate drawing

    // =====================================================================
    //  COORDINATE TRANSFORM  (map units  <->  screen pixels)
    //  We invent our own 2D world and place map-origin at screen centre.
    //  Pixel space here is bottom-left origin, +y up — same as GL and
    //  Input.mousePosition, so no axis flipping is needed for those.
    // =====================================================================
    Vector2 MapToPix(Vector2 m) => new Vector2(Screen.width * 0.5f + m.x * ppu,
                                               Screen.height * 0.5f + m.y * ppu);
    Vector2 PixToMap(Vector2 p) => new Vector2((p.x - Screen.width * 0.5f) / ppu,
                                               (p.y - Screen.height * 0.5f) / ppu);

    // =====================================================================
    //  MATHS HELPERS
    // =====================================================================

    // Compass bearing of a direction vector, degrees clockwise from North (+y).
    //   WHY: navigators measure angles clockwise from north, not the usual math
    //   convention (counter-clockwise from +x). atan2(x, y) instead of the usual
    //   atan2(y, x) swaps the axes to give exactly that.
    static float Bearing(Vector2 dir) =>
        Mathf.Repeat(Mathf.Atan2(dir.x, dir.y) * Mathf.Rad2Deg, 360f);

    // Unit direction vector for a compass bearing (inverse of Bearing()).
    static Vector2 Dir(float bearingDeg)
    {
        float r = bearingDeg * Mathf.Deg2Rad;
        return new Vector2(Mathf.Sin(r), Mathf.Cos(r)); // note: (sin, cos), not (cos, sin)
    }

    // 2D "cross product": the z of the 3D cross of (u,0) x (v,0).
    // It equals |u||v|sin(angle). Zero => u and v are parallel.
    static float Cross(Vector2 u, Vector2 v) => u.x * v.y - u.y * v.x;

    // Intersect two lines given as point+direction:  a + t*da  =  b + s*db.
    //   WHY it works: subtract to get (a-b) + t*da - s*db = 0. Cross both sides
    //   with db (which kills the s*db term because db x db = 0) and solve for t:
    //       t = ((b - a) x db) / (da x db)
    //   The denominator (da x db) is zero exactly when the lines are parallel.
    static bool Intersect(Vector2 a, Vector2 da, Vector2 b, Vector2 db, out Vector2 hit)
    {
        float denom = Cross(da, db);
        if (Mathf.Abs(denom) < 1e-6f) { hit = default; return false; } // parallel
        float t = Cross(b - a, db) / denom;
        hit = a + t * da;
        return true;
    }

    // A measured bearing from a station to the true target, plus Gaussian noise.
    float MeasuredBearing(int stationIdx, float unitGauss)
        => Bearing(pts[2] - pts[stationIdx]) + unitGauss * noiseDeg;

    // =====================================================================
    //  INPUT  —  drag points around
    // =====================================================================
    Rect panelRect = new Rect(10, 10, 640, 540); // OnGUI panel, in top-left coords

    void Update()
    {
        ppu = Mathf.Min(Screen.width, Screen.height) / mapSpan;
        Vector2 mouse = (Vector2)Input.mousePosition; // bottom-left origin, +y up

        // Ignore world-drags that start on top of the UI panel. panelRect is in
        // GUI space (top-left origin), so flip mouse.y to compare.
        bool overPanel = panelRect.Contains(new Vector2(mouse.x, Screen.height - mouse.y));

        if (Input.GetMouseButtonDown(0) && !overPanel)
        {
            dragIdx = -1;
            float best = 22f; // pixel pick radius
            for (int i = 0; i < pts.Length; i++)
            {
                float d = Vector2.Distance(mouse, MapToPix(pts[i]));
                if (d < best) { best = d; dragIdx = i; }
            }
        }
        if (Input.GetMouseButton(0) && dragIdx >= 0) pts[dragIdx] = PixToMap(mouse);
        if (Input.GetMouseButtonUp(0)) dragIdx = -1;
    }

    // =====================================================================
    //  DRAWING  (GL immediate mode, in pixel space)
    // =====================================================================
    void EnsureMat()
    {
        if (glMat != null) return;
        glMat = new Material(Shader.Find("Hidden/Internal-Colored")) { hideFlags = HideFlags.HideAndDontSave };
        glMat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        glMat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        glMat.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
        glMat.SetInt("_ZWrite", 0);
        glMat.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
    }

    void Line(Vector2 aMap, Vector2 bMap, Color c, float width = 2f)
    {
        Vector2 pa = MapToPix(aMap), pb = MapToPix(bMap);
        Vector2 dir = (pb - pa).sqrMagnitude > 1e-6f ? (pb - pa).normalized : Vector2.right;
        Vector2 perp = new Vector2(-dir.y, dir.x); // 90° rotation, to fake thickness
        GL.Color(c);
        for (float o = -width * 0.5f; o <= width * 0.5f; o += 1f)
        {
            Vector2 off = perp * o;
            GL.Vertex3(pa.x + off.x, pa.y + off.y, 0);
            GL.Vertex3(pb.x + off.x, pb.y + off.y, 0);
        }
    }

    void Marker(Vector2 mMap, Color c, float half = 6f)
    {
        Vector2 p = MapToPix(mMap);
        GL.Color(c);
        // filled square via two triangles' worth of QUAD — drawn as line pairs is
        // fiddly, so we just draw a small plus + box outline with LINES.
        GL.Vertex3(p.x - half, p.y, 0); GL.Vertex3(p.x + half, p.y, 0);
        GL.Vertex3(p.x, p.y - half, 0); GL.Vertex3(p.x, p.y + half, 0);
        GL.Vertex3(p.x - half, p.y - half, 0); GL.Vertex3(p.x + half, p.y - half, 0);
        GL.Vertex3(p.x + half, p.y - half, 0); GL.Vertex3(p.x + half, p.y + half, 0);
        GL.Vertex3(p.x + half, p.y + half, 0); GL.Vertex3(p.x - half, p.y + half, 0);
        GL.Vertex3(p.x - half, p.y + half, 0); GL.Vertex3(p.x - half, p.y - half, 0);
    }

    void Grid()
    {
        GL.Color(new Color(1, 1, 1, 0.06f));
        for (int i = -12; i <= 12; i += 2)
        {
            Vector2 a = MapToPix(new Vector2(i, -13)), b = MapToPix(new Vector2(i, 13));
            GL.Vertex3(a.x, a.y, 0); GL.Vertex3(b.x, b.y, 0);
            Vector2 c = MapToPix(new Vector2(-13, i)), d = MapToPix(new Vector2(13, i));
            GL.Vertex3(c.x, c.y, 0); GL.Vertex3(d.x, d.y, 0);
        }
    }

    void OnPostRender()
    {
        EnsureMat();
        GL.PushMatrix();
        glMat.SetPass(0);
        GL.LoadPixelMatrix();      // 2D pixel coordinate system, bottom-left origin

        GL.Begin(GL.LINES);
        Grid();

        Vector2 A = pts[0], B = pts[1], T = pts[2];
        Vector2 dA = Dir(MeasuredBearing(0, gA));
        Vector2 dB = Dir(MeasuredBearing(1, gB));

        Color colA = new Color(0.30f, 0.75f, 1.00f);   // A = blue
        Color colB = new Color(1.00f, 0.55f, 0.25f);   // B = orange
        Color colBase = new Color(1, 1, 1, 0.35f);     // baseline A-B
        Color colTrue = new Color(0.35f, 1.00f, 0.45f);// true target = green
        Color colEst = new Color(1.00f, 0.25f, 0.45f); // estimate  = red

        // Baseline between the two known stations.
        Line(A, B, colBase, 2f);

        // Bearing lines: shoot each measured bearing far across the map.
        Line(A, A + dA * 40f, colA, 2f);
        Line(B, B + dB * 40f, colB, 2f);

        // Markers.
        Marker(A, colA, 7f);
        Marker(B, colB, 7f);
        Marker(T, colTrue, 7f);
        if (Intersect(A, dA, B, dB, out Vector2 est)) Marker(est, colEst, 9f);

        GL.End();
        GL.PopMatrix();
    }

    // =====================================================================
    //  READOUT PANEL + floating labels
    // =====================================================================
    GUIStyle label;

    void Lbl(Vector2 mMap, string text, Color c)
    {
        Vector2 p = MapToPix(mMap);
        // Box must be at least as tall as the font, or the glyphs get clipped.
        float h = label.fontSize + 12f;
        // GL/pixel space is +y up, but OnGUI is +y down → flip y for placement.
        var r = new Rect(p.x + 10, Screen.height - p.y - h * 0.5f, 260, h);
        var s = new GUIStyle(label); s.normal.textColor = c; s.alignment = TextAnchor.MiddleLeft;
        GUI.Label(r, text, s);
    }

    void OnGUI()
    {
        if (label == null)
            label = new GUIStyle(GUI.skin.label) { fontSize = 26, fontStyle = FontStyle.Bold };

        Vector2 A = pts[0], B = pts[1], T = pts[2];

        // --- the measurements & the fix ---
        float bA = MeasuredBearing(0, gA);
        float bB = MeasuredBearing(1, gB);
        Vector2 dA = Dir(bA), dB = Dir(bB);
        bool ok = Intersect(A, dA, B, dB, out Vector2 est);

        // Law of sines pieces (see panel text below for the meaning).
        float c = Vector2.Distance(A, B);                 // baseline length
        float angA = Vector2.Angle(B - A, dA);            // interior angle at A
        float angB = Vector2.Angle(A - B, dB);            // interior angle at B
        float angT = 180f - angA - angB;                  // remaining angle at target
        float err = ok ? Vector2.Distance(est, T) : -1f;

        // --- floating labels on the map ---
        Lbl(A, "A", new Color(0.30f, 0.75f, 1f));
        Lbl(B, "B", new Color(1f, 0.55f, 0.25f));
        Lbl(T, "true", new Color(0.35f, 1f, 0.45f));
        if (ok) Lbl(est, "fix", new Color(1f, 0.25f, 0.45f));

        // --- readout panel ---
        GUI.Box(panelRect, "");
        GUILayout.BeginArea(new Rect(panelRect.x + 10, panelRect.y + 8, panelRect.width - 20, panelRect.height - 16));
        GUILayout.Label("<b>Method 1 — Triangulation (bearings)</b>", Rich());
        GUILayout.Space(2);
        GUILayout.Label($"Bearing A→target : {bA:0.0}°", label);
        GUILayout.Label($"Bearing B→target : {bB:0.0}°", label);
        GUILayout.Label($"Baseline A–B     : {c:0.00} u", label);
        GUILayout.Label($"Angles  α={angA:0.0}°  β={angB:0.0}°  γ={angT:0.0}°", label);
        if (ok)
        {
            GUILayout.Label($"Fix (x,y) : ({est.x:0.00}, {est.y:0.00})", label);
            GUILayout.Label($"Error vs true : {err:0.000} u", Colored(err));
        }
        else GUILayout.Label("Bearings parallel — no fix", Colored(99));

        GUILayout.Space(6);
        GUILayout.Label($"Bearing noise: {noiseDeg:0.0}°", label);
        noiseDeg = GUILayout.HorizontalSlider(noiseDeg, 0f, 15f, GUILayout.Height(30));
        GUILayout.Space(6);
        if (GUILayout.Button("New noise sample", Btn(), GUILayout.Height(50)))
        {
            // fresh unit-normal samples via Box–Muller (two uniforms -> two normals)
            float u1 = Mathf.Max(1e-6f, Random.value), u2 = Random.value;
            float mag = Mathf.Sqrt(-2f * Mathf.Log(u1));
            gA = mag * Mathf.Cos(2f * Mathf.PI * u2);
            gB = mag * Mathf.Sin(2f * Mathf.PI * u2);
            clicks++;
            Log($"CLICK #{clicks}  noise={noiseDeg:0.00}  gA={gA:0.000}  gB={gB:0.000}");
        }

        // TEMP DIAGNOSTIC readout — remove once we've proven the cause.
        GUILayout.Label($"[dbg] clicks={clicks}  gA={gA:0.00}  gB={gB:0.00}  noise={noiseDeg:0.0}", label);
        GUILayout.EndArea();
    }

    GUIStyle _rich, _col, _btn;
    GUIStyle Rich() { if (_rich == null) _rich = new GUIStyle(GUI.skin.label) { richText = true, fontSize = 26 }; return _rich; }
    GUIStyle Btn() { if (_btn == null) _btn = new GUIStyle(GUI.skin.button) { fontSize = 26 }; return _btn; }
    GUIStyle Colored(float err)
    {
        if (_col == null) _col = new GUIStyle(GUI.skin.label) { fontSize = 26, fontStyle = FontStyle.Bold };
        _col.normal.textColor = err < 0 ? Color.gray : Color.Lerp(Color.green, Color.red, Mathf.Clamp01(err / 3f));
        return _col;
    }
}
