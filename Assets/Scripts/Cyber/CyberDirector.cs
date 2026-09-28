using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace AmongUsClone
{
    /// <summary>
    /// CYBER: DOUBLE THREAT simulation director.
    /// Self-installs after scene load; runs host-side (solo investigator = host).
    /// Multiplayer sync of the event feed is a documented TODO (wrap Emit in Fusion RPCs).
    /// </summary>
    public sealed class CyberDirector : MonoBehaviour
    {
        public static CyberDirector I { get; private set; }

        public const int TotalRounds = 5;
        public const float RoundSeconds = 180f;
        public const float GameMinPerSec = 2f;          // 1 real sec = 2 sim minutes
        public const int StartBudget = 100;
        public const int MaxBudget = 150;

        public const int CostContain = 10, CostIsolate = 10, CostBlockIP = 5,
                         CostRestore = 15, CostAccuse = 20, MaxAccusationsPerRound = 2;

        // ---- read-only state for UI ----
        public int Round { get; private set; } = 1;
        public int Budget { get; private set; } = StartBudget;
        public int Score { get; private set; }
        public bool GameOver { get; private set; }
        public string OutcomeText { get; private set; } = "";
        public string Toast { get; private set; } = "";
        public bool IpBlocked { get; private set; }
        public int AccusationsUsed { get; private set; }
        public readonly List<SecurityEvent> Events = new List<SecurityEvent>();
        public readonly List<EmployeeProfile> Employees = new List<EmployeeProfile>();
        public readonly List<EnterpriseSystemState> Systems = new List<EnterpriseSystemState>();
        public bool UiDirty { get; set; } = true;

        // ---- internals ----
        ShipMapDefinition map;
        Rect[] roomRects, corridorRects, doorwayRects, obstacleRects;
        struct RoomInfo { public string id, name; public Rect rect; }
        List<RoomInfo> rooms = new List<RoomInfo>();
        sealed class Avatar { public GameObject go; public SpriteRenderer sr; public Vector2 pos, target; public float retarget; public Color color; }
        List<Avatar> avatars = new List<Avatar>();
        readonly Dictionary<int, AttackStep[]> chains = new Dictionary<int, AttackStep[]>();
        float roundTimer, clockMin = 8f * 60f, noiseTimer, toastTimer, difficulty = 1f;
        int nextEventId = 1, offlineCaused;
        const float Speed = 3.2f, Radius = 0.35f;

        static readonly string[] RoomEventCodes = { "4624", "4688", "DNS", "POLICY", "SERVICE", "FILE", "CASE" };

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void AutoInstall()
        {
            if (!CyberRules.CyberEmployeesEnabled || I != null) return;
            var go = new GameObject("CyberDirector");
            I = go.AddComponent<CyberDirector>();
            go.AddComponent<SocConsoleUI>();
        }

        void Awake()
        {
            I = this;
            LoadMap();
            BuildSystems();
            SpawnEmployees();
            StartRound(true);
        }

        // ---------------- map ----------------
        void LoadMap()
        {
            var ta = Resources.Load<TextAsset>("Maps/enterprise_soc_01");
            map = ta != null ? JsonUtility.FromJson<ShipMapDefinition>(ta.text) : null;
            if (map == null) { Debug.LogError("[Cyber] Map JSON not found"); enabled = false; return; }
            foreach (var r in map.rooms) rooms.Add(new RoomInfo { id = r.id, name = r.displayName, rect = r.bounds.ToRect() });
            roomRects = map.rooms.Select(r => r.bounds.ToRect()).ToArray();
            corridorRects = (map.corridors ?? new RectFeatureDefinition[0]).Select(r => r.bounds.ToRect()).ToArray();
            doorwayRects = (map.doorways ?? new RectFeatureDefinition[0]).Select(r => r.bounds.ToRect()).ToArray();
            obstacleRects = (map.obstacles ?? new RectFeatureDefinition[0]).Select(r => r.bounds.ToRect()).ToArray();
        }

        void BuildSystems()
        {
            string[] roomIds = { "iam", "endpoint", "network_ops", "firewall", "server_room", "data_storage", "soc" };
            string[] names = { "IAM / Identity", "Endpoints", "Network", "Firewall", "Servers", "Data Storage", "SIEM" };
            for (int i = 0; i < 7; i++)
                Systems.Add(new EnterpriseSystemState { system = (CyberSystem)i, roomId = roomIds[i], displayName = names[i] });
        }

        // ---------------- employees ----------------
        void SpawnEmployees()
        {
            var tex = new Texture2D(32, 32, TextureFormat.RGBA32, false);
            for (int y = 0; y < 32; y++) for (int x = 0; x < 32; x++)
                tex.SetPixel(x, y, Vector2.Distance(new Vector2(x, y), new Vector2(15.5f, 15.5f)) < 14f ? Color.white : Color.clear);
            tex.Apply();
            var sprite = Sprite.Create(tex, new Rect(0, 0, 32, 32), new Vector2(0.5f, 0.5f));

            var rnd = new System.Random();
            for (int i = 0; i < EmployeeRoster.Names.Length; i++)
            {
                var dept = EmployeeRoster.Departments[i];
                var p = new EmployeeProfile
                {
                    name = EmployeeRoster.Names[i], jobTitle = EmployeeRoster.Jobs[i], department = dept,
                    normalRooms = EmployeeRoster.NormalRoomsFor(dept), normalApps = EmployeeRoster.NormalAppsFor(dept)
                };
                Employees.Add(p);

                var pos = Vector2.zero;
                if (map.spawnPoints != null && map.spawnPoints.Length > i) pos = map.spawnPoints[i].position.ToVector2();
                var go = new GameObject("Emp_" + p.name);
                var sr = go.AddComponent<SpriteRenderer>();
                var hue = (i * 0.1f) % 1f;
                sr.sprite = sprite;
                sr.color = Color.HSVToRGB(hue, 0.55f, 0.95f);
                go.transform.position = pos;
                avatars.Add(new Avatar { go = go, sr = sr, pos = pos, target = pos, retarget = (float)rnd.NextDouble() * 6f, color = sr.color });
                p.currentRoomId = RoomAt(pos) ?? "soc";
            }
        }

        string RoomAt(Vector2 pos)
        {
            foreach (var r in rooms) if (r.rect.Contains(pos)) return r.id;
            return null;
        }

        Vector2 RandomPointInRoom(string roomId, System.Random rnd)
        {
            var r = rooms.FirstOrDefault(x => x.id == roomId);
            if (r.rect.width <= 0) r = rooms[0];
            return new Vector2(Mathf.Lerp(r.rect.xMin + 1.5f, r.rect.xMax - 1.5f, (float)rnd.NextDouble()),
                               Mathf.Lerp(r.rect.yMin + 1.5f, r.rect.yMax - 1.5f, (float)rnd.NextDouble()));
        }

        // ---------------- rounds / chains ----------------
        void StartRound(bool first)
        {
            roundTimer = RoundSeconds;
            IpBlocked = false; AccusationsUsed = 0; offlineCaused = 0;
            foreach (var s in Systems) s.health = SystemHealth.Online;
            foreach (var p in Employees)
            {
                p.accountContained = p.endpointIsolated = p.accused = p.neutralized = false;
                p.stage = AttackStage.Idle; p.chainId = -1; p.chainStep = 0; p.chainTimer = 0f;
            }
            chains.Clear();
            var rnd = new System.Random();
            var threats = Employees.OrderBy(_ => rnd.Next()).Take(2).ToList();
            threats[0].hiddenState = HiddenState.Saboteur;
            threats[1].hiddenState = HiddenState.DataThief;
            foreach (var innocent in Employees.Except(threats)) innocent.hiddenState = HiddenState.Innocent;

            threats[0].chainId = 1; threats[0].stage = AttackStage.InitialAccess;
            threats[0].chainTimer = (first ? 20f : 15f) * difficulty;
            chains[1] = AttackChainLibrary.SaboteurChain();
            threats[1].chainId = 2; threats[1].stage = AttackStage.InitialAccess;
            threats[1].chainTimer = (first ? 35f : 28f) * difficulty;
            chains[2] = AttackChainLibrary.DataThiefChain();

            if (!first) ShowToast($"ROUND {Round} — new shift, attackers re-arming. Budget +40.");
        }

        void EndRound()
        {
            int protectedSystems = Systems.Count(s => !s.IsDown());
            Score += protectedSystems * 5;
            Budget = Mathf.Min(MaxBudget, Budget + 40);
            Round++;
            difficulty *= 0.85f; // chains get faster
            if (Round > TotalRounds)
            {
                GameOver = true;
                OutcomeText = $"VICTORY — You survived all {TotalRounds} shifts.\nFinal score: {Score}";
            }
            else StartRound(false);
            UiDirty = true;
        }

        void Lose(string why)
        {
            GameOver = true;
            OutcomeText = $"BREACH — {why}\nFinal score: {Score}";
            ShowToast(OutcomeText); UiDirty = true;
        }

        void StopChain(EmployeeProfile p, string reason, int bonus)
        {
            if (p.stage == AttackStage.Contained || p.stage == AttackStage.Stopped) return;
            p.stage = p.accountContained || p.endpointIsolated || p.neutralized ? AttackStage.Contained : AttackStage.Stopped;
            if (bonus != 0) Score += bonus;
            ShowToast($"Chain stopped ({reason}): {p.name}  +{bonus}");
            if (Employees.All(e => e.hiddenState == HiddenState.Innocent || e.stage == AttackStage.Contained
                                || e.stage == AttackStage.Stopped || e.stage == AttackStage.Complete))
            { Score += 10; EndRound(); }
        }

        // ---------------- event emission ----------------
        public SecurityEvent Emit(CyberSystem sys, string roomId, string source, string code,
            EventSeverity sev, string employee, string msg, bool attack, int chain)
        {
            var e = new SecurityEvent
            {
                id = nextEventId++, gameTime = RoundSeconds - roundTimer, round = Round,
                clock = $"{(int)(clockMin / 60f):00}:{(int)(clockMin % 60f):00}",
                system = sys, roomId = roomId ?? "", source = source, eventCode = code,
                severity = sev, employee = employee, message = msg, isAttack = attack, chainId = chain
            };
            Events.Add(e);
            if (Events.Count > 400) Events.RemoveRange(0, Events.Count - 400);
            UiDirty = true;
            return e;
        }

        void Damage(CyberSystem sys)
        {
            var s = Systems[(int)sys];
            s.health = s.health == SystemHealth.Online ? SystemHealth.Compromised : SystemHealth.Offline;
            if (s.health == SystemHealth.Offline) offlineCaused++;
            if (offlineCaused >= 3) Lose("3 systems knocked offline — Saboteur objective complete.");
        }

        // ---------------- actions ----------------
        public bool TryAction(ContainActionType a, string empName, CyberSystem sys, out string msg)
        {
            msg = "";
            if (GameOver) { msg = "Game over."; return false; }
            var p = Employees.FirstOrDefault(x => x.name == empName);
            switch (a)
            {
                case ContainActionType.ContainAccount:
                    if (p == null || p.accountContained) { msg = "Invalid target."; return false; }
                    if (!Spend(CostContain, out msg)) return false;
                    p.accountContained = true;
                    Emit(CyberSystem.IAM, "iam", "Entra", "CONTAIN", EventSeverity.Warning, p.name, "Account disabled by SOC", false, -1);
                    if (p.hiddenState != HiddenState.Innocent && ActiveChain(p)) StopChain(p, "account contained", 15);
                    else { Score -= 5; msg = $"{p.name} contained. Productivity hit."; return true; }
                    return true;
                case ContainActionType.IsolateEndpoint:
                    if (p == null || p.endpointIsolated) { msg = "Invalid target."; return false; }
                    if (!Spend(CostIsolate, out msg)) return false;
                    p.endpointIsolated = true;
                    Emit(CyberSystem.Endpoint, p.currentRoomId, "EDR", "ISOLATE", EventSeverity.Warning, p.name, "Endpoint isolated by SOC", false, -1);
                    if (p.hiddenState != HiddenState.Innocent && ActiveChain(p) && p.stage <= AttackStage.LateralMovement)
                        StopChain(p, "endpoint isolated", 15);
                    else { Score -= 5; msg = $"{p.name}'s endpoint isolated. Productivity hit."; return true; }
                    return true;
                case ContainActionType.BlockIP:
                    if (!Spend(CostBlockIP, out msg)) return false;
                    IpBlocked = true;
                    ShowToast("IP 94.140.11.7 blocked at perimeter.");
                    return true;
                case ContainActionType.RestoreSystem:
                    var st = Systems[(int)sys];
                    if (st.health == SystemHealth.Online) { msg = "System already online."; return false; }
                    if (!Spend(CostRestore, out msg)) return false;
                    st.health = SystemHealth.Online; offlineCaused = Systems.Count(s => s.health == SystemHealth.Offline);
                    ShowToast($"{st.displayName} restored.");
                    return true;
                case ContainActionType.Accuse:
                    if (p == null || p.accused) { msg = "Invalid target."; return false; }
                    if (AccusationsUsed >= MaxAccusationsPerRound) { msg = "No accusations left this round."; return false; }
                    if (!Spend(CostAccuse, out msg)) return false;
                    AccusationsUsed++; p.accused = true;
                    if (p.hiddenState != HiddenState.Innocent)
                    {
                        p.neutralized = true; Score += 30;
                        StopChain(p, "threat actor identified", 0);
                        ShowToast($"CORRECT — {p.name} was a {p.hiddenState}. +30");
                    }
                    else { p.wrongfullyAccused = true; Score -= 25; ShowToast($"WRONG — {p.name} is innocent. -25"); }
                    return true;
            }
            return false;
        }

        bool ActiveChain(EmployeeProfile p) =>
            p.stage == AttackStage.InitialAccess || p.stage == AttackStage.PrivEsc ||
            p.stage == AttackStage.LateralMovement || p.stage == AttackStage.Objective;

        bool Spend(int cost, out string msg)
        {
            msg = "";
            if (Budget < cost) { msg = "Insufficient budget."; return false; }
            Budget -= cost; return true;
        }

        void ShowToast(string s) { Toast = s; toastTimer = 5f; UiDirty = true; }

        // ---------------- main tick ----------------
        void Update()
        {
            if (GameOver || map == null) return;
            float dt = Time.deltaTime;
            roundTimer -= dt; clockMin += dt * GameMinPerSec;
            if (toastTimer > 0) { toastTimer -= dt; if (toastTimer <= 0) { Toast = ""; UiDirty = true; } }
            if (roundTimer <= 0) { EndRound(); return; }

            MoveAvatars(dt);
            TickChains(dt);
            AmbientNoise(dt);
        }

        void MoveAvatars(dt)
        {
            var rnd = new System.Random();
            for (int i = 0; i < avatars.Count; i++)
            {
                var av = avatars[i]; var p = Employees[i];
                av.retarget -= dt;
                if (av.retarget <= 0)
                {
                    av.retarget = 5f + (float)rnd.NextDouble() * 5f;
                    string roomId;
                    if (ActiveChain(p) && p.chainStep < chains[p.chainId].Length)
                        roomId = chains[p.chainId][p.chainStep].roomId;         // attacker walks to target system
                    else if (ActiveChain(p)) roomId = p.normalRooms[rnd.Next(p.normalRooms.Count)];
                    else roomId = p.normalRooms[rnd.Next(p.normalRooms.Count)];
                    av.target = RandomPointInRoom(roomId, rnd);
                }
                var step = (av.target - av.pos);
                if (step.sqrMagnitude > 0.05f)
                {
                    var dir = step.normalized * Speed * dt;
                    var np = av.pos + dir;
                    if (!ShipMapGeometry.IsBlocked(np, Radius, roomRects, corridorRects, doorwayRects, obstacleRects)) av.pos = np;
                    else if (!ShipMapGeometry.IsBlocked(av.pos + new Vector2(dir.x, 0), Radius, roomRects, corridorRects, doorwayRects, obstacleRects)) av.pos += new Vector2(dir.x, 0);
                    else if (!ShipMapGeometry.IsBlocked(av.pos + new Vector2(0, dir.y), Radius, roomRects, corridorRects, doorwayRects, obstacleRects)) av.pos += new Vector2(0, dir.y);
                    av.go.transform.position = av.pos;
                }
                var room = RoomAt(av.pos);
                if (room != null && room != p.currentRoomId && !p.neutralized)
                {
                    p.currentRoomId = room;
                    Emit((CyberSystem)Mathf.Min(6, Array.IndexOf(RoomEventCodes, "CASE")),
                        room, "Sysmon", "ROOM", EventSeverity.Info, p.name, $"{p.name} entered {RoomName(room)}", false, -1);
                }
            }
        }

        string RoomName(string id) { var r = rooms.FirstOrDefault(x => x.id == id); return r.id == null ? id : r.name; }

        void TickChains(float dt)
        {
            foreach (var p in Employees)
            {
                if (p.hiddenState == HiddenState.Innocent || !ActiveChain(p)) continue;
                if (p.accountContained || p.endpointIsolated) { StopChain(p, "access cut", 0); continue; }
                p.chainTimer -= dt;
                if (p.chainTimer > 0) continue;
                var steps = chains[p.chainId];
                if (p.chainStep >= steps.Length)
                {
                    if (p.hiddenState == HiddenState.DataThief) Lose("Ransomware detonated and data exfiltrated.");
                    else p.stage = AttackStage.Complete;
                    continue;
                }
                var s = steps[p.chainStep];
                p.chainTimer = s.delay * difficulty;
                p.chainStep++;
                p.stage = p.chainStep <= 2 ? AttackStage.InitialAccess
                        : p.chainStep <= 5 ? AttackStage.PrivEsc
                        : p.chainStep <= 8 ? AttackStage.LateralMovement : AttackStage.Objective;

                if (!string.IsNullOrEmpty(s.counteredByBlockIP) && IpBlocked)
                {
                    Emit(s.system, s.roomId, "Firewall", "DENY", EventSeverity.Warning, p.name,
                        "Outbound channel blocked by SOC deny rule — attack channel closed", false, -1);
                    StopChain(p, "egress blocked", 15);
                    continue;
                }
                Emit(s.system, s.roomId, s.source, s.eventCode, s.severity, p.name, s.message, true, p.chainId);
                if (s.damagesSystem) Damage(s.system);
            }
        }

        void AmbientNoise(float dt)
        {
            noiseTimer -= dt;
            if (noiseTimer > 0) return;
            noiseTimer = 2.5f + UnityEngine.Random.value * 2.5f;
            var innocent = Employees.Where(p => p.hiddenState == HiddenState.Innocent && !p.accountContained).OrderBy(_ => UnityEngine.Random.value).FirstOrDefault();
            if (innocent == null) return;
            var room = innocent.currentRoomId;
            var sys = room == "iam" ? CyberSystem.IAM : room == "network_ops" ? CyberSystem.Network
                    : room == "data_storage" ? CyberSystem.Data : room == "server_room" ? CyberSystem.Server
                    : room == "firewall" ? CyberSystem.Firewall : CyberSystem.Endpoint;
            var app = innocent.normalApps[UnityEngine.Random.Range(0, innocent.normalApps.Count)];
            Emit(sys, room, "Sysmon", "4688", EventSeverity.Info, innocent.name, $"{app} launched (baseline activity)", false, -1);
        }

        public int ThreatsActive => Employees.Count(p => ActiveChain(p));
    }
}
