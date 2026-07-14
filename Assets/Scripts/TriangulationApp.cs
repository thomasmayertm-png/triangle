using UnityEngine;

// =============================================================================
//  POSITION-FIXING PLAYGROUND
//  Method 1: TRIANGULATION (bearings)   Method 2: TRILATERATION (ranges / GPS)
// -----------------------------------------------------------------------------
//  METHOD 1 — each of two known stations measures a BEARING (compass angle) to
//  the target. The target lies on each bearing line, so the fix is where the two
//  lines cross. Reconstructed via line intersection == the law of sines.
//
//  METHOD 2 — each of THREE known stations measures a DISTANCE (range) to the
//  target. Draw a circle of that radius around each station; the target is where
//  the circles meet. This is how GPS works (satellites = stations, radio travel
//  time = range). Two circles are ambiguous (they cross in two places); the third
//  circle pins down the single answer.
//
//  Drag any station or the true target; every line, circle and number updates
//  live. The NOISE slider corrupts the measurements so the clean crossing point
//  turns fuzzy — the motivation for the least-squares method to come.
//
//  Switch methods with the buttons in the panel. "Show working" reveals the
//  underlying geometry (the triangle for M1, the radical lines for M2).
// =============================================================================
public class TriangulationApp : MonoBehaviour
{
    // ---- Bootstrap: build camera + this component when Play starts ----------
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        var go = new GameObject("PositionFixingApp");
        var cam = go.AddComponent<Camera>();
        cam.orthographic = true;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.07f, 0.08f, 0.10f);
        cam.transform.position = new Vector3(0, 0, -10);
        go.tag = "MainCamera";
        go.AddComponent<TriangulationApp>();
    }

    // ---- Model state (map units ~ metres) -----------------------------------
    //  pts[0]=A  pts[1]=B  pts[2]=TRUE target  pts[3]=C
    //  (target kept at index 2 so Method-1 code is unchanged; C is index 3.)
    Vector2[] pts = { new Vector2(-6f, -4f), new Vector2(6f, -4f), new Vector2(1.5f, 5f), new Vector2(-3f, 8f) };

    int method = 1;                      // 1 = bearings, 2 = ranges
    bool showWork = false;               // reveal the underlying geometry
    bool showExplain = false;            // show the plain-language explanation panel

    float noiseDeg = 0f;                 // Method-1 bearing noise, std-dev degrees
    float noiseUnit = 0f;                // Method-2 range   noise, std-dev units
    float noiseTdoa = 0f;                // Method-3 range-difference noise, std-dev units
    float noiseLS = 1.0f;                // Method-4 range noise, std-dev units (starts >0 so the ellipse is visible)
    float gA = 0.6f, gB = -0.9f, gC = 0.4f; // fixed unit-normal samples per station

    int dragIdx = -1;
    const float mapSpan = 26f;
    float ppu;

    Material glMat;

    // Colours reused by both the GL drawing and the text labels.
    static readonly Color cA = new Color(0.30f, 0.75f, 1.00f);   // Station A  blue
    static readonly Color cB = new Color(1.00f, 0.55f, 0.25f);   // Station B  orange
    static readonly Color cC = new Color(1.00f, 0.85f, 0.25f);   // Station C  yellow
    static readonly Color cTrue = new Color(0.35f, 1.00f, 0.45f);// true       green
    static readonly Color cEst = new Color(1.00f, 0.25f, 0.45f); // fix        red
    static readonly Color cBase = new Color(1, 1, 1, 0.30f);

    // Station indices that the current method uses (target is always index 2).
    int[] Stations() => method == 1 ? new[] { 0, 1 } : new[] { 0, 1, 3 };

    // =====================================================================
    //  COORDINATE TRANSFORM  (map units <-> screen pixels, +y up like GL)
    // =====================================================================
    Vector2 MapToPix(Vector2 m) => new Vector2(Screen.width * 0.5f + m.x * ppu,
                                               Screen.height * 0.5f + m.y * ppu);
    Vector2 PixToMap(Vector2 p) => new Vector2((p.x - Screen.width * 0.5f) / ppu,
                                               (p.y - Screen.height * 0.5f) / ppu);

    // =====================================================================
    //  MATHS HELPERS
    // =====================================================================

    // Compass bearing (deg clockwise from North/+y). atan2(x,y) swaps the usual
    // axes so 0°=North and angles increase clockwise, like a real compass.
    static float Bearing(Vector2 dir) =>
        Mathf.Repeat(Mathf.Atan2(dir.x, dir.y) * Mathf.Rad2Deg, 360f);
    static Vector2 Dir(float bearingDeg)
    {
        float r = bearingDeg * Mathf.Deg2Rad;
        return new Vector2(Mathf.Sin(r), Mathf.Cos(r));
    }

    // 2D cross product = |u||v|sin(angle); zero when u,v parallel.
    static float Cross(Vector2 u, Vector2 v) => u.x * v.y - u.y * v.x;

    // Line-line intersection a+t*da = b+s*db. Cross with db kills the s term:
    //   t = ((b-a) x db) / (da x db). Denominator 0 => parallel (no fix).
    static bool Intersect(Vector2 a, Vector2 da, Vector2 b, Vector2 db, out Vector2 hit)
    {
        float denom = Cross(da, db);
        if (Mathf.Abs(denom) < 1e-6f) { hit = default; return false; }
        float t = Cross(b - a, db) / denom;
        hit = a + t * da;
        return true;
    }

    float MeasuredBearing(int station, float g) => Bearing(pts[2] - pts[station]) + g * noiseDeg;
    float MeasuredRange(int station, float g) =>
        Mathf.Max(0.05f, Vector2.Distance(pts[station], pts[2]) + g * noiseUnit);

    // TRILATERATION via linearisation.  Each circle: (x-xi)^2+(y-yi)^2 = ri^2.
    // Subtracting circle 0 from circle k cancels the x^2 and y^2 terms (identical
    // in every circle), leaving a STRAIGHT line ("radical line"):
    //   2(xk-x0)x + 2(yk-y0)y = r0^2 - rk^2 + (xk^2+yk^2) - (x0^2+y0^2)
    // Two such lines (k=1,2) give a 2x2 system; solve with Cramer's rule.
    // WHY it's nice: squaring made it non-linear, subtracting made it linear again.
    bool Trilaterate(int s0, int s1, int s2, out Vector2 fix)
    {
        Vector2 p0 = pts[s0], p1 = pts[s1], p2 = pts[s2];
        float r0 = MeasuredRange(s0, gA), r1 = MeasuredRange(s1, gB), r2 = MeasuredRange(s2, gC);

        // Row k:  ak . (x,y) = dk
        float a1x = 2 * (p1.x - p0.x), a1y = 2 * (p1.y - p0.y);
        float a2x = 2 * (p2.x - p0.x), a2y = 2 * (p2.y - p0.y);
        float d1 = r0 * r0 - r1 * r1 + (p1.sqrMagnitude - p0.sqrMagnitude);
        float d2 = r0 * r0 - r2 * r2 + (p2.sqrMagnitude - p0.sqrMagnitude);

        float det = a1x * a2y - a1y * a2x; // stations collinear => det ~ 0
        if (Mathf.Abs(det) < 1e-6f) { fix = default; return false; }
        fix = new Vector2((d1 * a2y - d2 * a1y) / det,
                          (a1x * d2 - a2x * d1) / det);
        return true;
    }

    // Radical line for the pair (s0,sk): normal n = ak, offset d = dk. Returned as
    // point+direction so we can draw it. (Used only by "show working".)
    void RadicalLine(int s0, int sk, float rk, out Vector2 mid, out Vector2 dir)
    {
        Vector2 p0 = pts[s0], pk = pts[sk];
        float r0 = MeasuredRange(s0, s0 == 0 ? gA : s0 == 1 ? gB : gC);
        Vector2 n = new Vector2(2 * (pk.x - p0.x), 2 * (pk.y - p0.y));
        float d = r0 * r0 - rk * rk + (pk.sqrMagnitude - p0.sqrMagnitude);
        float len2 = Mathf.Max(1e-6f, n.sqrMagnitude);
        mid = n * (d / len2);                 // closest point on the line to origin
        dir = new Vector2(-n.y, n.x).normalized; // perpendicular to the normal
    }

    // =====================================================================
    //  INPUT — drag the active points
    // =====================================================================
    Rect panelRect = new Rect(10, 10, 660, 620);

    void Update()
    {
        ppu = Mathf.Min(Screen.width, Screen.height) / mapSpan;
        Vector2 mouse = (Vector2)Input.mousePosition;
        bool overPanel = panelRect.Contains(new Vector2(mouse.x, Screen.height - mouse.y));

        if (Input.GetMouseButtonDown(0) && !overPanel)
        {
            dragIdx = -1;
            float best = 24f;
            // active = current method's stations + the true target (index 2)
            foreach (int i in Active())
            {
                float d = Vector2.Distance(mouse, MapToPix(pts[i]));
                if (d < best) { best = d; dragIdx = i; }
            }
        }
        if (Input.GetMouseButton(0) && dragIdx >= 0) pts[dragIdx] = PixToMap(mouse);
        if (Input.GetMouseButtonUp(0)) dragIdx = -1;
    }

    System.Collections.Generic.IEnumerable<int> Active()
    {
        foreach (int s in Stations()) yield return s;
        yield return 2; // target
    }

    // =====================================================================
    //  GL DRAWING
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
        Vector2 perp = new Vector2(-dir.y, dir.x);
        GL.Color(c);
        for (float o = -width * 0.5f; o <= width * 0.5f; o += 1f)
        {
            Vector2 off = perp * o;
            GL.Vertex3(pa.x + off.x, pa.y + off.y, 0);
            GL.Vertex3(pb.x + off.x, pb.y + off.y, 0);
        }
    }

    void Circle(Vector2 cMap, float rMap, Color col, int seg = 96)
    {
        GL.Color(col);
        Vector2 prev = cMap + new Vector2(rMap, 0);
        for (int i = 1; i <= seg; i++)
        {
            float a = i / (float)seg * 2f * Mathf.PI;
            Vector2 cur = cMap + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * rMap;
            Vector2 pp = MapToPix(prev), pc = MapToPix(cur);
            GL.Vertex3(pp.x, pp.y, 0); GL.Vertex3(pc.x, pc.y, 0);
            prev = cur;
        }
    }

    void Marker(Vector2 mMap, Color c, float half = 7f)
    {
        Vector2 p = MapToPix(mMap);
        GL.Color(c);
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
        GL.LoadPixelMatrix();
        GL.Begin(GL.LINES);

        Grid();
        if (method == 1) DrawMethod1();
        else if (method == 2) DrawMethod2();
        else if (method == 3) DrawMethod3();
        else DrawMethod4();
        Marker(pts[2], cTrue, 7f); // true target on top in every method

        GL.End();
        GL.PopMatrix();
    }

    void DrawMethod1()
    {
        Vector2 A = pts[0], B = pts[1];
        Vector2 dA = Dir(MeasuredBearing(0, gA));
        Vector2 dB = Dir(MeasuredBearing(1, gB));

        Line(A, B, cBase, 2f);
        Line(A, A + dA * 40f, cA, 2f);
        Line(B, B + dB * 40f, cB, 2f);
        Marker(A, cA); Marker(B, cB);

        if (Intersect(A, dA, B, dB, out Vector2 fix))
        {
            Marker(fix, cEst, 9f);
            if (showWork) { Line(A, fix, new Color(1, 1, 1, 0.5f), 1f); Line(B, fix, new Color(1, 1, 1, 0.5f), 1f); }
        }
    }

    void DrawMethod2()
    {
        int[] s = { 0, 1, 3 };
        float[] r = { MeasuredRange(0, gA), MeasuredRange(1, gB), MeasuredRange(3, gC) };
        Color[] col = { cA, cB, cC };
        for (int i = 0; i < 3; i++) { Circle(pts[s[i]], r[i], col[i]); Marker(pts[s[i]], col[i]); }

        if (showWork)
        {
            // radical lines from circle 0 vs circle 1, and circle 0 vs circle 2
            RadicalLine(0, 1, r[1], out Vector2 m1, out Vector2 d1);
            RadicalLine(0, 3, r[2], out Vector2 m2, out Vector2 d2);
            Line(m1 - d1 * 30f, m1 + d1 * 30f, new Color(1, 1, 1, 0.45f), 1f);
            Line(m2 - d2 * 30f, m2 + d2 * 30f, new Color(1, 1, 1, 0.45f), 1f);
        }
        if (Trilaterate(0, 1, 3, out Vector2 fix)) Marker(fix, cEst, 9f);
    }

    void DrawMethod3()
    {
        Vector2 A = pts[0], B = pts[1], C = pts[3];
        Marker(A, cA); Marker(B, cB); Marker(C, cC);

        float dAB = MeasuredDiff(0, 1, gA);   // range difference dA - dB
        float dAC = MeasuredDiff(0, 3, gB);   // range difference dA - dC

        // Each range difference draws one hyperbola with the pair as its foci.
        Hyperbola(A, B, dAB, cB, showWork);   // coloured by the "other" focus
        Hyperbola(A, C, dAC, cC, showWork);

        if (SolveTDOA(out Vector2 fix)) Marker(fix, cEst, 9f);
    }

    // Measured range DIFFERENCE dA - dB, with Gaussian noise (the TDOA observable).
    float MeasuredDiff(int sA, int sB, float g)
        => (Vector2.Distance(pts[2], pts[sA]) - Vector2.Distance(pts[2], pts[sB])) + g * noiseTdoa;

    // Draw the branch of the hyperbola { p : dist(p,F1) - dist(p,F2) = delta }.
    //   Center M = midpoint of foci; half-focal-separation c = |F1F2|/2.
    //   Semi-axis a = delta/2 (sign selects which branch, i.e. which focus is nearer).
    //   b = sqrt(c^2 - a^2). Points parametrise as (a*cosh t, b*sinh t) in the
    //   frame u = F1->F2, uP = perpendicular. |a| < c always (triangle inequality),
    //   so b is real.
    void Hyperbola(Vector2 F1, Vector2 F2, float delta, Color col, bool asymptotes)
    {
        Vector2 axis = F2 - F1;
        float focal = axis.magnitude;
        if (focal < 1e-4f) return;
        Vector2 u = axis / focal, uP = new Vector2(-u.y, u.x);
        Vector2 M = (F1 + F2) * 0.5f;
        float c = focal * 0.5f;
        float a = Mathf.Clamp(delta * 0.5f, -c * 0.999f, c * 0.999f);
        float b = Mathf.Sqrt(Mathf.Max(0f, c * c - a * a));

        const int seg = 90; const float tmax = 2.3f;
        Vector2 prev = default; bool has = false;
        for (int i = 0; i <= seg; i++)
        {
            float t = -tmax + 2f * tmax * i / seg;
            float lx = a * (float)System.Math.Cosh(t);
            float ly = b * (float)System.Math.Sinh(t);
            Vector2 w = M + u * lx + uP * ly;
            if (has) Line(prev, w, col, 2f);
            prev = w; has = true;
        }

        if (asymptotes)
        {
            // Hyperbola hugs two straight lines through M with local slope ±b/a.
            Vector2 d1 = (u * Mathf.Abs(a) + uP * b).normalized;
            Vector2 d2 = (u * Mathf.Abs(a) - uP * b).normalized;
            Color faint = new Color(1, 1, 1, 0.30f);
            Line(M - d1 * 30f, M + d1 * 30f, faint, 1f);
            Line(M - d2 * 30f, M + d2 * 30f, faint, 1f);
        }
    }

    // TDOA fix by Gauss-Newton. Residuals are the two range-difference equations:
    //   r1(p) = (|p-A| - |p-B|) - dAB ,  r2(p) = (|p-A| - |p-C|) - dAC.
    // The gradient of |p-A| is the unit vector A->p, so each residual's gradient is
    //   grad r = unit(p-A) - unit(p-Si).  We stack these into J and step
    //   p <- p - (JtJ)^-1 Jt r  until it stops moving. Start at the station centroid.
    bool SolveTDOA(out Vector2 fix)
    {
        Vector2 A = pts[0], B = pts[1], C = pts[3];
        float dAB = MeasuredDiff(0, 1, gA), dAC = MeasuredDiff(0, 3, gB);
        Vector2 p = (A + B + C) / 3f;

        for (int it = 0; it < 60; it++)
        {
            Vector2 uA = (p - A).normalized, uB = (p - B).normalized, uC = (p - C).normalized;
            float r1 = (Vector2.Distance(p, A) - Vector2.Distance(p, B)) - dAB;
            float r2 = (Vector2.Distance(p, A) - Vector2.Distance(p, C)) - dAC;
            Vector2 j1 = uA - uB, j2 = uA - uC;      // residual gradients (rows of J)

            // 2x2 normal equations  (JtJ) dp = -(Jt r)
            float m00 = j1.x * j1.x + j2.x * j2.x;
            float m01 = j1.x * j1.y + j2.x * j2.y;
            float m11 = j1.y * j1.y + j2.y * j2.y;
            float g0 = j1.x * r1 + j2.x * r2;
            float g1 = j1.y * r1 + j2.y * r2;
            float det = m00 * m11 - m01 * m01;
            if (Mathf.Abs(det) < 1e-9f) break;       // degenerate geometry
            float dx = -(m11 * g0 - m01 * g1) / det;
            float dy = -(m00 * g1 - m01 * g0) / det;
            p += new Vector2(dx, dy);
            if (dx * dx + dy * dy < 1e-10f) break;   // converged
        }
        fix = p;
        return !(float.IsNaN(p.x) || float.IsNaN(p.y));
    }

    void DrawMethod4()
    {
        int[] s = { 0, 1, 3 };
        float[] g = { gA, gB, gC };
        Color[] col = { cA, cB, cC };
        for (int i = 0; i < 3; i++)
        {
            float r = Mathf.Max(0.05f, Vector2.Distance(pts[2], pts[s[i]]) + g[i] * noiseLS);
            Circle(pts[s[i]], r, col[i]);
            Marker(pts[s[i]], col[i]);
        }

        if (SolveLS(out Vector2 fix, out float cxx, out float cxy, out float cyy, out _))
        {
            Marker(fix, cEst, 9f);
            Eigen(cxx, cxy, cyy, out float sMaj, out float sMin, out float ang);
            // Draw the 2-sigma ellipse (semi-axes = 2 x the 1-sigma std devs).
            Ellipse(fix, 2f * sMaj, 2f * sMin, ang, new Color(1f, 0.25f, 0.45f, 0.9f));
        }
    }

    // LEAST-SQUARES range fix by Gauss-Newton over ALL three ranges (3 eqns, 2
    // unknowns -> over-determined, no exact solution once noisy).
    //   residual_i = |p - Si| - r_i ,  grad = unit(p - Si).
    // The normal matrix M = J^T J is the "information"; its inverse scaled by the
    // measurement variance is the COVARIANCE of the fix:  C = sigma^2 (J^T J)^-1.
    // Eigen-decomposing C gives the error-ellipse axes. (Good geometry -> small
    // round ellipse; poor geometry -> large stretched one = high DOP.)
    bool SolveLS(out Vector2 fix, out float cxx, out float cxy, out float cyy, out float rms)
    {
        int[] s = { 0, 1, 3 };
        float[] g = { gA, gB, gC };
        float[] r = new float[3];
        for (int i = 0; i < 3; i++) r[i] = Mathf.Max(0.05f, Vector2.Distance(pts[2], pts[s[i]]) + g[i] * noiseLS);

        Vector2 p = (pts[0] + pts[1] + pts[3]) / 3f;
        float m00 = 0, m01 = 0, m11 = 0, rss = 0;
        for (int it = 0; it < 60; it++)
        {
            m00 = m01 = m11 = 0; rss = 0; float gg0 = 0, gg1 = 0;
            for (int i = 0; i < 3; i++)
            {
                Vector2 d = p - pts[s[i]];
                float dist = d.magnitude;
                Vector2 u = dist > 1e-6f ? d / dist : Vector2.right;
                float res = dist - r[i];
                rss += res * res;
                m00 += u.x * u.x; m01 += u.x * u.y; m11 += u.y * u.y;
                gg0 += u.x * res; gg1 += u.y * res;
            }
            float det = m00 * m11 - m01 * m01;
            if (Mathf.Abs(det) < 1e-9f) { fix = p; cxx = cyy = cxy = 0; rms = Mathf.Sqrt(rss / 3f); return false; }
            float dx = -(m11 * gg0 - m01 * gg1) / det;
            float dy = -(m00 * gg1 - m01 * gg0) / det;
            p += new Vector2(dx, dy);
            if (dx * dx + dy * dy < 1e-10f) break;
        }

        rms = Mathf.Sqrt(rss / 3f);
        float sigma = Mathf.Max(noiseLS, 1e-4f);         // assumed measurement std
        float dt = m00 * m11 - m01 * m01, inv = 1f / dt; // (J^T J)^-1 = adj/det
        cxx = sigma * sigma * m11 * inv;
        cxy = -sigma * sigma * m01 * inv;
        cyy = sigma * sigma * m00 * inv;
        fix = p;
        return !(float.IsNaN(p.x) || float.IsNaN(p.y));
    }

    // Eigen-decomposition of the symmetric 2x2 covariance [[cxx,cxy],[cxy,cyy]].
    // Returns the two 1-sigma semi-axes (sqrt of eigenvalues) and the tilt angle.
    void Eigen(float cxx, float cxy, float cyy, out float sMaj, out float sMin, out float ang)
    {
        float tr = cxx + cyy, det = cxx * cyy - cxy * cxy;
        float disc = Mathf.Sqrt(Mathf.Max(0f, tr * tr * 0.25f - det));
        sMaj = Mathf.Sqrt(Mathf.Max(0f, tr * 0.5f + disc));
        sMin = Mathf.Sqrt(Mathf.Max(0f, tr * 0.5f - disc));
        ang = 0.5f * Mathf.Atan2(2f * cxy, cxx - cyy);
    }

    void Ellipse(Vector2 c, float a, float b, float ang, Color col, int seg = 72)
    {
        float ca = Mathf.Cos(ang), sa = Mathf.Sin(ang);
        Vector2 prev = default; bool has = false;
        for (int i = 0; i <= seg; i++)
        {
            float t = i / (float)seg * 2f * Mathf.PI;
            float lx = a * Mathf.Cos(t), ly = b * Mathf.Sin(t);
            Vector2 w = c + new Vector2(ca * lx - sa * ly, sa * lx + ca * ly); // rotate then offset
            if (has) Line(prev, w, col, 2f);
            prev = w; has = true;
        }
    }

    // =====================================================================
    //  UI  (readout panel + floating labels)
    // =====================================================================
    GUIStyle label;

    void Lbl(Vector2 mMap, string text, Color c)
    {
        Vector2 p = MapToPix(mMap);
        float h = label.fontSize + 12f;
        var r = new Rect(p.x + 10, Screen.height - p.y - h * 0.5f, 260, h);
        var st = new GUIStyle(label); st.normal.textColor = c; st.alignment = TextAnchor.MiddleLeft;
        GUI.Label(r, text, st);
    }

    void OnGUI()
    {
        if (label == null) label = new GUIStyle(GUI.skin.label) { fontSize = 26, fontStyle = FontStyle.Bold };

        // floating labels
        Lbl(pts[0], "A", cA);
        Lbl(pts[1], "B", cB);
        if (method != 1) Lbl(pts[3], "C", cC);
        Lbl(pts[2], "true", cTrue);

        GUI.Box(panelRect, "");
        GUILayout.BeginArea(new Rect(panelRect.x + 12, panelRect.y + 10, panelRect.width - 24, panelRect.height - 20));

        // method switch
        GUILayout.BeginHorizontal();
        if (GUILayout.Toggle(method == 1, "M1 bearings", Btn(), GUILayout.Height(46)) && method != 1) method = 1;
        if (GUILayout.Toggle(method == 2, "M2 ranges", Btn(), GUILayout.Height(46)) && method != 2) method = 2;
        GUILayout.EndHorizontal();
        GUILayout.BeginHorizontal();
        if (GUILayout.Toggle(method == 3, "M3 TDOA", Btn(), GUILayout.Height(46)) && method != 3) method = 3;
        if (GUILayout.Toggle(method == 4, "M4 best-fit", Btn(), GUILayout.Height(46)) && method != 4) method = 4;
        GUILayout.EndHorizontal();
        GUILayout.Space(6);

        if (method == 1) Panel1(); else if (method == 2) Panel2(); else if (method == 3) Panel3(); else Panel4();

        GUILayout.Space(8);
        GUILayout.BeginHorizontal();
        showWork = GUILayout.Toggle(showWork, "Show working", Btn(), GUILayout.Height(40));
        showExplain = GUILayout.Toggle(showExplain, "Explain", Btn(), GUILayout.Height(40));
        GUILayout.EndHorizontal();
        GUILayout.EndArea();

        if (showExplain) DrawExplain();
    }

    // Second panel, to the right of the main one: what the current method does, in words.
    void DrawExplain()
    {
        var box = new Rect(panelRect.xMax + 12, panelRect.y, 640, 560);
        GUI.Box(box, "");
        var area = new Rect(box.x + 14, box.y + 12, box.width - 28, box.height - 24);
        GUILayout.BeginArea(area);
        GUILayout.Label(ExplainText(), Wrap());
        GUILayout.EndArea();
    }

    string ExplainText()
    {
        if (method == 1) return
            "<b>Triangulation — crossing two bearings</b>\n\n" +
            "Each station (A, B) measures only the DIRECTION to the target — a compass " +
            "bearing — not the distance. So the target lies somewhere along A's bearing " +
            "line, and also somewhere along B's. The one point on both lines is where " +
            "they cross: the fix (red dot).\n\n" +
            "The angles alpha, beta, gamma are the corners of triangle A-B-target. Given " +
            "the baseline A-B and two angles, the LAW OF SINES rebuilds the other sides — " +
            "the same crossing point, reached by algebra instead of by eye.\n\n" +
            "Add bearing noise: each line tilts a few degrees, so the crossing slips away " +
            "from the true target (green). Two lines ALWAYS meet at exactly one point, so " +
            "nothing warns you the answer is wrong — the weakness of using just two lines.";

        if (method == 2) return
            "<b>Trilateration — three range circles (GPS)</b>\n\n" +
            "Each station now measures DISTANCE to the target (from signal travel time), " +
            "but not direction. One distance means 'the target is on a circle of that " +
            "radius around me.' Two circles cross in two places — ambiguous. A third " +
            "circle selects the single point common to all three: the fix. This is exactly " +
            "how GPS works, with satellites playing the part of the stations.\n\n" +
            "The trick: a circle equation is curved (it has x-squared and y-squared terms). " +
            "Subtract one circle from another and those squared terms CANCEL, leaving a " +
            "straight line. Two straight lines cross at the answer with plain arithmetic — " +
            "no searching. Turn on 'Show working' to see those lines.\n\n" +
            "Raise the range noise and each radius drifts, so the three circles no longer " +
            "share a point. There is no exact solution any more — the very reason the " +
            "best-fit method (Method 4) has to exist.";

        if (method == 3) return
            "<b>Multilateration — TDOA hyperbolas</b>\n\n" +
            "Here we can measure neither distance nor direction — only the DIFFERENCE in " +
            "arrival time between a pair of stations: how much FARTHER the target is from " +
            "one station than from the other. The points with a fixed difference of " +
            "distances to two fixed points form a HYPERBOLA whose foci are those two " +
            "stations. Pair (A,B) gives one hyperbola, pair (A,C) another; the target sits " +
            "where they cross.\n\n" +
            "Because distances are square roots, the equations won't cancel the way circles " +
            "did. So we start from a guess and take repeated straight-line steps that " +
            "shrink the error (Gauss-Newton) until it settles on the crossing.\n\n" +
            "This locates a phone from cell-tower timing, and drove old LORAN navigation. " +
            "Add noise and the hyperbolas shift, sliding the crossing off the true target.";

        return
            "<b>Least-squares fit + error ellipse</b>\n\n" +
            "In the real world every measurement is noisy, so the three range circles never " +
            "meet at one point — there is no exact answer. Instead we look for the position " +
            "that comes CLOSEST to all of them at once: the point minimising the total " +
            "squared miss (least squares). With 3 measurements and 2 unknowns the problem " +
            "is over-determined, which is exactly what lets the errors average out.\n\n" +
            "The same computation hands us the UNCERTAINTY for free. The matrix of station " +
            "directions tells us how measurement noise spreads into position error; its " +
            "shape, scaled by the noise, is the covariance. Drawing that as an ellipse (red) " +
            "shows where the true point probably lies.\n\n" +
            "Notice the shape: stations spread widely around the target give a small round " +
            "ellipse; stations bunched on one side give a long stretched one — the same " +
            "noise, but worse geometry. Surveyors call this 'dilution of precision'. Slide " +
            "the noise up and down and drag the stations to feel the ellipse breathe.";
    }

    GUIStyle _wrap;
    GUIStyle Wrap()
    {
        if (_wrap == null) _wrap = new GUIStyle(GUI.skin.label) { fontSize = 22, richText = true, wordWrap = true };
        return _wrap;
    }

    void Panel1()
    {
        Vector2 A = pts[0], B = pts[1], T = pts[2];
        float bA = MeasuredBearing(0, gA), bB = MeasuredBearing(1, gB);
        Vector2 dA = Dir(bA), dB = Dir(bB);
        bool ok = Intersect(A, dA, B, dB, out Vector2 fix);
        float c = Vector2.Distance(A, B);
        float angA = Vector2.Angle(B - A, dA), angB = Vector2.Angle(A - B, dB);
        float angT = 180f - angA - angB;
        float err = ok ? Vector2.Distance(fix, T) : -1f;

        GUILayout.Label("<b>Triangulation — two bearings cross</b>", Rich());
        GUILayout.Label($"Bearing A→target : {bA:0.0}°", label);
        GUILayout.Label($"Bearing B→target : {bB:0.0}°", label);
        GUILayout.Label($"Baseline A–B : {c:0.00} u", label);
        GUILayout.Label($"Angles  α={angA:0.0}°  β={angB:0.0}°  γ={angT:0.0}°", label);
        Fix(ok, fix, err);
        NoiseRow(ref noiseDeg, 0f, 15f, "Bearing noise", "°");
    }

    void Panel2()
    {
        Vector2 T = pts[2];
        float rA = MeasuredRange(0, gA), rB = MeasuredRange(1, gB), rC = MeasuredRange(3, gC);
        bool ok = Trilaterate(0, 1, 3, out Vector2 fix);
        float err = ok ? Vector2.Distance(fix, T) : -1f;

        GUILayout.Label("<b>Trilateration — three range circles (GPS)</b>", Rich());
        GUILayout.Label($"Range A : {rA:0.00} u", label);
        GUILayout.Label($"Range B : {rB:0.00} u", label);
        GUILayout.Label($"Range C : {rC:0.00} u", label);
        Fix(ok, fix, err);
        NoiseRow(ref noiseUnit, 0f, 3f, "Range noise", " u");
    }

    void Panel3()
    {
        Vector2 T = pts[2];
        float dAB = MeasuredDiff(0, 1, gA), dAC = MeasuredDiff(0, 3, gB);
        bool ok = SolveTDOA(out Vector2 fix);
        float err = ok ? Vector2.Distance(fix, T) : -1f;

        GUILayout.Label("<b>Multilateration — TDOA hyperbolas</b>", Rich());
        GUILayout.Label($"Range diff dA-dB : {dAB:0.00} u", label);
        GUILayout.Label($"Range diff dA-dC : {dAC:0.00} u", label);
        Fix(ok, fix, err);
        NoiseRow(ref noiseTdoa, 0f, 2f, "TDOA noise", " u");
    }

    void Panel4()
    {
        GUILayout.Label("<b>Least-squares fit + error ellipse</b>", Rich());
        bool ok = SolveLS(out Vector2 fix, out float cxx, out float cxy, out float cyy, out float rms);
        if (!ok)
        {
            GUILayout.Label("Degenerate geometry — no fit", Colored(-1));
            NoiseRow(ref noiseLS, 0f, 3f, "Meas. noise", " u");
            return;
        }
        Eigen(cxx, cxy, cyy, out float sMaj, out float sMin, out float ang);
        float err = Vector2.Distance(fix, pts[2]);

        GUILayout.Label($"Best fit (x,y) : ({fix.x:0.00}, {fix.y:0.00})", label);
        GUILayout.Label($"Error vs true : {err:0.000} u", Colored(err));
        GUILayout.Label($"RMS residual : {rms:0.000} u", label);
        GUILayout.Label($"Ellipse 1σ : {sMaj:0.00} × {sMin:0.00} u", label);
        GUILayout.Label($"Ellipse tilt : {ang * Mathf.Rad2Deg:0.0}°", label);
        NoiseRow(ref noiseLS, 0f, 3f, "Meas. noise", " u");
    }

    void Fix(bool ok, Vector2 fix, float err)
    {
        if (ok)
        {
            GUILayout.Label($"Fix (x,y) : ({fix.x:0.00}, {fix.y:0.00})", label);
            GUILayout.Label($"Error vs true : {err:0.000} u", Colored(err));
        }
        else GUILayout.Label("Degenerate geometry — no fix", Colored(-1));
    }

    void NoiseRow(ref float noise, float lo, float hi, string name, string unit)
    {
        GUILayout.Space(6);
        GUILayout.Label($"{name}: {noise:0.0}{unit}", label);
        noise = GUILayout.HorizontalSlider(noise, lo, hi, GUILayout.Height(30));
        GUILayout.Space(6);
        if (GUILayout.Button("New noise sample", Btn(), GUILayout.Height(50)))
        {
            gA = Gauss(); gB = Gauss(); gC = Gauss();
        }
    }

    // Standard normal sample via Box–Muller (one uniform pair -> one normal).
    static float Gauss()
    {
        float u1 = Mathf.Max(1e-6f, Random.value), u2 = Random.value;
        return Mathf.Sqrt(-2f * Mathf.Log(u1)) * Mathf.Cos(2f * Mathf.PI * u2);
    }

    GUIStyle _rich, _col, _btn;
    GUIStyle Rich() { if (_rich == null) _rich = new GUIStyle(GUI.skin.label) { richText = true, fontSize = 26 }; return _rich; }
    GUIStyle Btn() { if (_btn == null) _btn = new GUIStyle(GUI.skin.button) { fontSize = 22 }; return _btn; }
    GUIStyle Colored(float err)
    {
        if (_col == null) _col = new GUIStyle(GUI.skin.label) { fontSize = 26, fontStyle = FontStyle.Bold };
        _col.normal.textColor = err < 0 ? Color.gray : Color.Lerp(Color.green, Color.red, Mathf.Clamp01(err / 3f));
        return _col;
    }
}
