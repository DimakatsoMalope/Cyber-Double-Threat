using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;

namespace AmongUsClone
{
    public sealed class SocConsoleUI : MonoBehaviour
    {
        Canvas canvas; Font font;
        Text header, detail, toast;
        Transform empList, feed, sysList;
        CyberSystem tab = CyberSystem.SIAM; // SIEM = all
        SecurityEvent selected;
        float refresh;
        readonly List<Button> rowButtons = new List<Button>();

        void Awake()
        {
            font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            var go = new GameObject("SocConsoleCanvas");
            canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 200;
            go.AddComponent<GraphicRaycaster>();
            if (FindFirstObjectByType<EventSystem>() == null) new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));

            var root = Panel(null, new Vector2(0, 0), new Vector2(1, 0.62f), new Color(0.04f, 0.05f, 0.08f, 0.94f));
            header = Text(root, "", 16, new Vector2(0, 0.9f), new Vector2(1, 1), 15, TextAnchor.MiddleLeft);
            toast = Text(root, "", 16, new Vector2(0.25f, 0.84f), new Vector2(0.75f, 0.9f), 14, TextAnchor.MiddleCenter);
            toast.color = new Color(1f, 0.8f, 0.3f);

            // tabs
            string[] tabs = { "SIEM", "IAM", "ENDPOINT", "NETWORK", "FIREWALL", "SERVER", "DATA", "TIMELINE" };
            for (int i = 0; i < tabs.Length; i++)
            {
                int idx = i; var b = Btn(root, tabs[i], new Vector2(0.24f + i * 0.07f, 0.8f), new Vector2(0.31f + i * 0.07f, 0.86f), () => SetTab(idx));
            }
            // employee column
            Text(root, "EMPLOYEES", 14, new Vector2(0.005f, 0.74f), new Vector2(0.2f, 0.8f), 12, TextAnchor.MiddleLeft).color = Color.cyan;
            empList = Panel(root, new Vector2(0.005f, 0.1f), new Vector2(0.2f, 0.74f), new Color(1, 1, 1, 0.04f)).transform;
            // feed
            feed = Panel(root, new Vector2(0.21f, 0.1f), new Vector2(0.72f, 0.8f), new Color(1, 1, 1, 0.04f)).transform;
            detail = Text(root, "Select an event to inspect + correlate.", 14, new Vector2(0.21f, 0.01f), new Vector2(0.72f, 0.09f), 13, TextAnchor.UpperLeft);
            // systems + actions
            Text(root, "SYSTEMS / ACTIONS", 14, new Vector2(0.73f, 0.74f), new Vector2(0.995f, 0.8f), 12, TextAnchor.MiddleLeft).color = Color.cyan;
            sysList = Panel(root, new Vector2(0.73f, 0.3f), new Vector2(0.995f, 0.74f), new Color(1, 1, 1, 0.04f)).transform;
            string[] actions = { "Contain Account (10)", "Isolate Endpoint (10)", "Block IP (5)", "Restore System (15)", "ACCUSE (20)" };
            for (int i = 0; i < actions.Length; i++)
            {
                int a = i;
                Btn(root, actions[i], new Vector2(0.73f, 0.22f - i * 0.05f), new Vector2(0.995f, 0.27f - i * 0.05f), () => DoAction(a));
            }
            canvas.enabled = true;
        }

        void SetTab(int i) { tab = (CyberSystem)i; if (i == 7) tab = CyberSystem.SIEM; CyberDirector.I.UiDirty = true; }
        string SelectedEmp { get; set; } = "Sarah";

        void DoAction(int a)
        {
            var d = CyberDirector.I;
            string msg;
            switch (a)
            {
                case 0: d.TryAction(ContainActionType.ContainAccount, SelectedEmp, 0, out msg); break;
                case 1: d.TryAction(ContainActionType.IsolateEndpoint, SelectedEmp, 0, out msg); break;
                case 2: d.TryAction(ContainActionType.BlockIP, "", 0, out msg); break;
                case 3: d.TryAction(ContainActionType.RestoreSystem, "", WorstSystem(), out msg); break;
                default: d.TryAction(ContainActionType.Accuse, SelectedEmp, 0, out msg); break;
            }
            if (!string.IsNullOrEmpty(msg)) d.UiDirty = true;
        }

        CyberSystem WorstSystem()
        {
            var d = CyberDirector.I;
            var s = d.Systems.FirstOrDefault(x => x.health == SystemHealth.Offline) ?? d.Systems.FirstOrDefault(x => x.health == SystemHealth.Compromised);
            return s != null ? s.system : CyberSystem.Server;
        }

        void Update()
        {
            if (Input.GetKeyDown(KeyCode.Tab)) canvas.enabled = !canvas.enabled;
            var d = CyberDirector.I;
            if (d == null || !canvas.enabled) return;
            refresh -= Time.deltaTime;
            if (refresh > 0 && !d.UiDirty) return;
            refresh = 0.3f; d.UiDirty = false;

            header.text = $"  CYBER: DOUBLE THREAT   |   ROUND {d.Round}/{CyberDirector.TotalRounds}   |   SHIFT {Clock(d)}   |   " +
                          $"BUDGET {d.Budget}   |   SCORE {d.Score}   |   ACTIVE THREATS: {d.ThreatsActive}   |   [Tab] console";
            toast.text = d.Toast;

            if (d.GameOver) { detail.text = d.OutcomeText; return; }

            // employees
            foreach (Transform c in empList) Destroy(c.gameObject);
            foreach (var p in d.Employees.OrderBy(p => p.name))
            {
                string tag = p.neutralized ? "☠ NEUTRALIZED" : p.accused ? (p.wrongfullyAccused ? "✗ innocent" : "✔ caught")
                              : p.accountContained ? "⏸ contained" : p.endpointIsolated ? "⏸ isolated" : "• " + p.currentRoomId;
                var b = Btn(empList.gameObject, $"{p.name,-8} {tag}", () => { SelectedEmp = p.name; });
                var colors = b.colors;
                if (p.hiddenState != HiddenState.Innocent && (p.neutralized || p.accused)) colors.normalColor = new Color(1f, 0.4f, 0.4f);
                else if (p.name == SelectedEmp) colors.normalColor = new Color(0.3f, 0.6f, 1f);
                b.colors = colors;
            }

            // feed
            foreach (Transform c in feed) Destroy(c.gameObject);
            var q = d.Events.Where(e => tab == CyberSystem.SIEM || e.system == tab).OrderByDescending(e => e.id).Take(28).Reverse();
            foreach (var e in q)
            {
                var sev = e.severity == EventSeverity.Critical ? "🔴" : e.severity == EventSeverity.Warning ? "🟡" : "⚪";
                var b = Btn(feed.gameObject, $"{e.clock} {sev} [{e.eventCode}] {e.employee}: {e.message}", () => Select(e));
                if (selected != null && Correlated(e, selected))
                { var c = b.colors; c.normalColor = new Color(0.5f, 0.25f, 0.7f); b.colors = c; }
            }

            // systems
            foreach (Transform c in sysList) Destroy(c.gameObject);
            foreach (var s in d.Systems)
            {
                var col = s.health == SystemHealth.Online ? Color.green : s.health == SystemHealth.Compromised ? new Color(1f, 0.6f, 0.1f) : Color.red;
                var t = Text(sysList.gameObject, $"{s.displayName}: {s.health}", 12, col);
            }
            Text(sysList.gameObject, $"Target: {SelectedEmp}  |  IP block: {(d.IpBlocked ? "ACTIVE" : "—")}  |  Accusations left: {CyberDirector.MaxAccusationsPerRound - d.AccusationsUsed}", 11, Color.gray);
        }

        static string Clock(CyberDirector d)
        {
            float m = 8f * 60f + (CyberDirector.RoundSeconds - 0) * 0 + (Time.timeSinceLevelLoad * CyberDirector.GameMinPerSec);
            return $"{(int)(m / 60f):00}:{(int)(m % 60f):00}";
        }

        void Select(SecurityEvent e)
        {
            selected = e;
            var d = CyberDirector.I;
            detail.text = $"{e.clock}  {e.source} / {e.eventCode}  [{e.severity}]  room: {e.roomId}\n{e.employee}: {e.message}\nCorrelated (same actor ±90s) highlighted purple.";
            d.UiDirty = true;
        }

        static bool Correlated(SecurityEvent a, SecurityEvent b) =>
            a.employee == b.employee && Mathf.Abs(a.gameTime - b.gameTime) <= 90f;

        // ---- builders ----
        GameObject Panel(GameObject parent, Vector2 aMin, Vector2 aMax, Color c)
        {
            var go = new GameObject("P", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent == null ? canvas.transform : parent.transform, false);
            var rt = (RectTransform)go.transform; rt.anchorMin = aMin; rt.anchorMax = aMax;
            rt.offsetMin = rt.offsetMax = Vector2.zero;
            go.GetComponent<Image>().color = c;
            return go;
        }

        Text Text(GameObject parent, string s, int pad, Vector2 aMin, Vector2 aMax, int size, TextAnchor anchor = TextAnchor.MiddleLeft)
        {
            var go = new GameObject("T", typeof(RectTransform), typeof(Text));
            go.transform.SetParent(parent.transform, false);
            var rt = (RectTransform)go.transform; rt.anchorMin = aMin; rt.anchorMax = aMax;
            rt.offsetMin = new Vector2(pad, 2); rt.offsetMax = new Vector2(-pad, -2);
            var t = go.GetComponent<Text>(); t.font = font; t.text = s; t.fontSize = size;
            t.color = Color.white; t.alignment = anchor; t.horizontalOverflow = HorizontalWrapMode.Wrap;
            return t;
        }

        Text Text(GameObject parent, string s, int size, Color c)
        {
            var go = new GameObject("T", typeof(RectTransform), typeof(Text), typeof(LayoutElement));
            go.transform.SetParent(parent.transform, false);
            go.GetComponent<LayoutElement>().preferredHeight = 22;
            var t = go.GetComponent<Text>(); t.font = font; t.text = s; t.fontSize = size;
            t.color = c; t.alignment = TextAnchor.MiddleLeft; t.horizontalOverflow = HorizontalWrapMode.Overflow;
            return t;
        }

        Button Btn(GameObject parent, string label, Vector2 aMin, Vector2 aMax, UnityEngine.Events.UnityAction onClick)
        {
            var go = new GameObject("B", typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent.transform, false);
            var rt = (RectTransform)go.transform; rt.anchorMin = aMin; rt.anchorMax = aMax;
            rt.offsetMin = new Vector2(2, 2); rt.offsetMax = new Vector2(-2, -2);
            go.GetComponent<Image>().color = new Color(1, 1, 1, 0.12f);
            var tgo = new GameObject("L", typeof(RectTransform), typeof(Text));
            tgo.transform.SetParent(go.transform, false);
            var trt = (RectTransform)tgo.transform; trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one;
            trt.offsetMin = new Vector2(8, 0); trt.offsetMax = new Vector2(-8, 0);
            var t = tgo.GetComponent<Text>(); t.font = font; t.text = label; t.fontSize = 12;
            t.color = Color.white; t.alignment = TextAnchor.MiddleLeft;
            var b = go.GetComponent<Button>(); b.onClick.AddListener(onClick);
            return b;
        }

        Button Btn(GameObject parent, string label, UnityEngine.Events.UnityAction onClick)
        {
            var go = new GameObject("B", typeof(RectTransform), typeof(Image), typeof(Button), typeof(HorizontalLayoutGroup));
            go.transform.SetParent(parent.transform, false);
            go.GetComponent<HorizontalLayoutGroup>().padding = new RectOffset(8, 4, 2, 2);
            go.GetComponent<Image>().color = new Color(1, 1, 1, 0.10f);
            var t = new GameObject("L", typeof(RectTransform), typeof(Text)).GetComponent<Text>();
            t.transform.SetParent(go.transform, false);
            t.font = font; t.text = label; t.fontSize = 12; t.color = Color.white;
            t.alignment = TextAnchor.MiddleLeft; t.horizontalOverflow = HorizontalWrapMode.Overflow;
            var le = t.gameObject.AddComponent<LayoutElement>(); le.preferredWidth = 400;
            var b = go.GetComponent<Button>(); b.onClick.AddListener(onClick);
            return b;
        }
    }
}
