using System;
using System.Collections.Generic;
using BepInEx;
using UnityEngine;

namespace RepoEsp
{
    [BepInPlugin("com.learn.repoesp", "REPO ESP", "0.3.2")]
    public class RepoEspPlugin : BaseUnityPlugin
    {
        void Awake()
        {
            gameObject.AddComponent<EspOverlay>();
            Logger.LogInfo("REPO ESP 已加载 (GL 渲染版，F6 开关)");
        }
    }

    public class EspOverlay : MonoBehaviour
    {
        // 全部用 static：即使这个组件被销毁，Camera.onPostRender 回调依然存活
        static bool draw = true;
        static bool inputBroken = false;
        static bool firstPostRender = true;
        static float lastStatusLog;
        static Type enemyType;
        static Material lineMat;
        static readonly HashSet<GameObject> seen = new HashSet<GameObject>();

        void Awake()
        {
            if (enemyType == null)
            {
                enemyType = FindTypeByName("EnemyParent");
                if (enemyType != null)
                    Debug.Log("[RepoEsp] 找到敌人类型: " + enemyType.FullName);
                else
                    Debug.Log("[RepoEsp] 未找到 EnemyParent，退回按名字 'Enemy' 搜索");

                Camera.onPostRender += OnPostRenderCallback;
                Debug.Log("[RepoEsp] 已订阅 Camera.onPostRender");
            }
        }

        static void OnPostRenderCallback(Camera cam)
        {
            // 只在主相机渲染后画一次
            if (Camera.main != null && cam != Camera.main) return;

            if (firstPostRender)
            {
                firstPostRender = false;
                Debug.Log("[RepoEsp] 第一次 OnPostRender, cam=" + cam.name +
                          " screen=" + Screen.width + "x" + Screen.height);
            }

            if (!inputBroken)
            {
                try { if (Input.GetKeyDown(KeyCode.F6)) draw = !draw; }
                catch
                {
                    inputBroken = true;
                    Debug.Log("[RepoEsp] 旧版 Input 不可用，F6 失效，保持常开");
                }
            }

            if (Time.unscaledTime - lastStatusLog > 5f)
            {
                lastStatusLog = Time.unscaledTime;
                LogStatus(cam);
            }

            if (!draw || cam == null) return;

            EnsureLineMat();
            if (lineMat == null) return;

            lineMat.SetPass(0);
            GL.PushMatrix();
            GL.LoadPixelMatrix();

            foreach (var go in GetEnemies())
            {
                if (go == null) continue;
                DrawEnemyGL(cam, go);
            }

            // 左上角青色 + 右上角洋红小方块：证明 OnPostRender 在跑（与怪无关）
            DrawMarkerGL();

            GL.PopMatrix();
        }

        static void LogStatus(Camera cam)
        {
            int enemyCount = 0, rendererCount = 0;
            foreach (var go in GetEnemies())
            {
                if (go == null) continue;
                enemyCount++;
                rendererCount += go.GetComponentsInChildren<Renderer>().Length;
            }
            Debug.Log($"[RepoEsp] status: draw={draw} cam={(cam != null ? cam.name : "NULL")} enemies={enemyCount} renderers={rendererCount}");
        }

        // ---------- 敌人枚举 ----------

        static IEnumerable<GameObject> GetEnemies()
        {
            seen.Clear();
            var list = new List<GameObject>();

            if (enemyType != null)
            {
                try
                {
                    foreach (var o in Resources.FindObjectsOfTypeAll(enemyType))
                    {
                        var c = o as Component;
                        if (c == null) continue;
                        var g = c.gameObject;
                        if (g == null || !g.scene.IsValid() || !g.activeInHierarchy) continue;
                        if (seen.Add(g)) list.Add(g);
                    }
                }
                catch (Exception e) { Debug.Log("[RepoEsp] 类型查找失败: " + e.Message); }
            }

            if (list.Count == 0)
            {
                foreach (var go in Resources.FindObjectsOfTypeAll<GameObject>())
                {
                    if (go == null) continue;
                    if (!go.scene.IsValid() || !go.activeInHierarchy) continue;
                    if (go.name.StartsWith("Enemy", StringComparison.OrdinalIgnoreCase))
                        if (seen.Add(go)) list.Add(go);
                }
            }

            foreach (var g in list) yield return g;
        }

        // ---------- GL 绘制 ----------

        static void DrawEnemyGL(Camera cam, GameObject go)
        {
            // 只合并"网格"的 bounds；粒子/拖尾等 Renderer 会把框撑得巨大
            Bounds? total = null;
            foreach (var r in go.GetComponentsInChildren<Renderer>())
            {
                if (r == null || !r.enabled) continue;
                if (!(r is MeshRenderer) && !(r is SkinnedMeshRenderer)) continue;
                if (total == null) total = r.bounds;
                else
                {
                    var b = total.Value;
                    b.Encapsulate(r.bounds);
                    total = b;
                }
            }

            // 骨骼节点
            var nodes = new List<Transform>();
            CollectByPrefix(go.transform, "ANIM", nodes);
            if (nodes.Count < 2)
            {
                nodes.Clear();
                var visuals = FindFirst(go.transform,
                    t => t.name.IndexOf("VISUALS", StringComparison.OrdinalIgnoreCase) >= 0);
                if (visuals != null) CollectAll(visuals, nodes);
            }

            if (total == null && nodes.Count == 0) return;

            if (total != null) DrawBoxGL(cam, total.Value, Color.green);
            DrawSkeletonGL(cam, nodes);
        }

        static void DrawBoxGL(Camera cam, Bounds b, Color col)
        {
            Vector3 c = b.center, e = b.extents;
            Vector3[] corners =
            {
                c + new Vector3(-e.x, -e.y, -e.z), c + new Vector3( e.x, -e.y, -e.z),
                c + new Vector3( e.x,  e.y, -e.z), c + new Vector3(-e.x,  e.y, -e.z),
                c + new Vector3(-e.x, -e.y,  e.z), c + new Vector3( e.x, -e.y,  e.z),
                c + new Vector3( e.x,  e.y,  e.z), c + new Vector3(-e.x,  e.y,  e.z),
            };

            float minX = float.MaxValue, minY = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue;

            foreach (var p in corners)
            {
                Vector3 sp = cam.WorldToScreenPoint(p);
                // 有角在镜头后，整个框跳过（避免乱线）
                if (sp.z < 0) return;
                if (sp.x < minX) minX = sp.x;
                if (sp.x > maxX) maxX = sp.x;
                if (sp.y < minY) minY = sp.y;
                if (sp.y > maxY) maxY = sp.y;
            }

            // 2D 屏幕矩形（标准 ESP 方框）
            GL.Begin(GL.LINES);
            GL.Color(col);
            GL.Vertex(new Vector3(minX, minY, 0)); GL.Vertex(new Vector3(maxX, minY, 0));
            GL.Vertex(new Vector3(maxX, minY, 0)); GL.Vertex(new Vector3(maxX, maxY, 0));
            GL.Vertex(new Vector3(maxX, maxY, 0)); GL.Vertex(new Vector3(minX, maxY, 0));
            GL.Vertex(new Vector3(minX, maxY, 0)); GL.Vertex(new Vector3(minX, minY, 0));
            GL.End();
        }

        static void DrawSkeletonGL(Camera cam, List<Transform> nodes)
        {
            if (nodes == null || nodes.Count == 0) return;

            var set = new HashSet<Transform>(nodes);

            GL.Begin(GL.LINES);
            GL.Color(Color.yellow);
            foreach (var n in nodes)
            {
                if (n == null) continue;
                Vector3 sp = cam.WorldToScreenPoint(n.position);
                if (sp.z < 0) continue;

                var p = n.parent;
                while (p != null && !set.Contains(p)) p = p.parent;
                if (p != null)
                {
                    Vector3 pp = cam.WorldToScreenPoint(p.position);
                    if (pp.z >= 0)
                    {
                        GL.Vertex(new Vector3(sp.x, sp.y, 0));
                        GL.Vertex(new Vector3(pp.x, pp.y, 0));
                    }
                }
            }
            GL.End();

            // 骨节点画小方块
            GL.Begin(GL.QUADS);
            GL.Color(Color.yellow);
            foreach (var n in nodes)
            {
                if (n == null) continue;
                Vector3 sp = cam.WorldToScreenPoint(n.position);
                if (sp.z < 0) continue;
                float s = 3f;
                GL.Vertex(new Vector3(sp.x - s, sp.y - s, 0));
                GL.Vertex(new Vector3(sp.x + s, sp.y - s, 0));
                GL.Vertex(new Vector3(sp.x + s, sp.y + s, 0));
                GL.Vertex(new Vector3(sp.x - s, sp.y + s, 0));
            }
            GL.End();
        }

        static void DrawMarkerGL()
        {
            float s = 24;

            // 左上角青色
            GL.Begin(GL.QUADS);
            GL.Color(new Color(0f, 1f, 1f, 0.9f));
            GL.Vertex(new Vector3(20, Screen.height - 20 - s, 0));
            GL.Vertex(new Vector3(20 + s, Screen.height - 20 - s, 0));
            GL.Vertex(new Vector3(20 + s, Screen.height - 20, 0));
            GL.Vertex(new Vector3(20, Screen.height - 20, 0));
            GL.End();

            // 右上角洋红
            GL.Begin(GL.QUADS);
            GL.Color(new Color(1f, 0f, 1f, 0.9f));
            GL.Vertex(new Vector3(Screen.width - 20 - s, Screen.height - 20 - s, 0));
            GL.Vertex(new Vector3(Screen.width - 20, Screen.height - 20 - s, 0));
            GL.Vertex(new Vector3(Screen.width - 20, Screen.height - 20, 0));
            GL.Vertex(new Vector3(Screen.width - 20 - s, Screen.height - 20, 0));
            GL.End();
        }

        static void EnsureLineMat()
        {
            if (lineMat != null) return;
            var shader = Shader.Find("Hidden/Internal-Colored");
            if (shader == null) shader = Shader.Find("Sprites/Default");
            if (shader == null)
            {
                Debug.Log("[RepoEsp] 找不到可用的 GL shader");
                return;
            }
            lineMat = new Material(shader);
            lineMat.hideFlags = HideFlags.HideAndDontSave;
            lineMat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            lineMat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            lineMat.SetInt("_Cull", 0);
            lineMat.SetInt("_ZWrite", 0);
        }

        // ---------- 工具 ----------

        static void CollectByPrefix(Transform t, string prefix, List<Transform> outList)
        {
            if (t.name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                outList.Add(t);
            for (int i = 0; i < t.childCount; i++)
                CollectByPrefix(t.GetChild(i), prefix, outList);
        }

        static void CollectAll(Transform t, List<Transform> outList)
        {
            outList.Add(t);
            for (int i = 0; i < t.childCount; i++)
                CollectAll(t.GetChild(i), outList);
        }

        static Transform FindFirst(Transform t, Func<Transform, bool> pred)
        {
            if (pred(t)) return t;
            for (int i = 0; i < t.childCount; i++)
            {
                var r = FindFirst(t.GetChild(i), pred);
                if (r != null) return r;
            }
            return null;
        }

        static Type FindTypeByName(string name)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch { continue; }

                foreach (var t in types)
                    if (t.Name == name) return t;
            }
            return null;
        }
    }
}
