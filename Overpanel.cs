using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Network;
using Oxide.Core;
using Oxide.Core.Libraries;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using UnityEngine;
using WebSocketSharp;

namespace Oxide.Plugins
{
    /// <summary>
    /// Overpanel — единый файл плагина. Все подсистемы (WebSocket-канал с
    /// панелью, наказания, проверки, RCON, аудио-уведомления, интеграции и
    /// т.д.) собраны здесь как один physical-файл, чтобы весь плагин падал
    /// и перезагружался одной командой (c.reload Overpanel / o.reload Overpanel),
    /// а не набором из отдельных партиал-файлов/плагинов.
    ///
    /// Структура ориентирована через #region — см. оглавление ниже:
    ///   Plugin Core & Lifecycle, Configuration, Data & Storage,
    ///   Connection & Pairing, WebSocket, Permissions & Admins, Access List,
    ///   Punishments, Checks, Audio, CUI Overlays, Reports & Player Commands,
    ///   RCON, Player Hooks & Chat, Integrations.
    /// </summary>
    [Info("Overpanel", "Gooseoma", "1.0.5")]
    [Description("Administrative panel integration for Rust servers")]
    public class Overpanel : RustPlugin
    {
        #region Plugin Core & Lifecycle

        public static Overpanel Instance { get; private set; }

        internal static bool _isCarbon;

        void Init()
        {
            Instance = this;
        }

        void OnServerInitialized()
        {
            _isCarbon = IsCarbon();

            if (!ValidateAudioFiles()) return;

            CreateDirectoryStructure();
            LoadLocalReportBackground();
            LoadConfig();
            InitPermissionsCache();
            InitWebSocket();
            DetectFramework();
            DetectIntegrations();
            InitRconCapture();
            InitAccessListScheduler();
            InitMapTimer();
            InitStatsTimer();
            InitYearlyRecap();
            RegisterCommands();

            Puts($"[Overpanel] v{PLUGIN_VERSION} загружен на {(_isCarbon ? "Carbon" : "Oxide/uMod")}");
        }

        void Unload()
        {
            ShutdownAudio();
            ShutdownRconCapture();
            ShutdownWebSocket();
            CleanupAll();
        }

        private bool IsCarbon()
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.GetName().Name == "Carbon.Common")
                    return true;
            }
            return false;
        }

        internal void UnloadPlugin(string name)
        {
            if (_isCarbon)
                Server.Command($"c.unload {name}");
            else
                Server.Command($"o.unload {name}");
        }

        private void RegisterCommands()
        {
            AddCovalenceCommand("report", nameof(CmdReport));
            AddCovalenceCommand("rules", nameof(CmdRules));
            AddCovalenceCommand("panel", nameof(CmdPanel));
            AddCovalenceCommand("discord", nameof(CmdDiscord));
        }

        private void DetectFramework()
        {
            PrintWarning($"Framework: {(_isCarbon ? "Carbon" : "Oxide/uMod")}");
        }

        // ── Статистика сервера ───────────────────────────────────────

        private Timer _statsTimer;
        private readonly DateTime _startedAt = DateTime.UtcNow;

        private void InitStatsTimer()
        {
            _statsTimer = timer.Every(30f, SendServerStats);
        }

        private void SendServerStats()
        {
            if (!IsBackendConnected) return;

            SendEvent("server.stats", new Dictionary<string, object>
            {
                ["online"]         = BasePlayer.activePlayerList.Count,
                ["max"]            = ConVar.Server.maxplayers,
                ["sleepers"]       = BasePlayer.sleepingPlayerList.Count,
                ["uptime"]         = (int)(DateTime.UtcNow - _startedAt).TotalSeconds,
                ["plugin_version"] = PLUGIN_VERSION,
                // Нужно панели, чтобы перевести x/z из player.position_batch в проценты на карте
                ["world_size"]     = ConVar.Server.worldsize,
                // Имя могли сменить после привязки — держим панель в курсе
                ["name"]           = GetEffectiveServerName(),
            });
        }

        /// <summary>
        /// Имя сервера для панели. ServerName в конфиге имеет дефолт "My Rust Server"
        /// и никогда не бывает null, поэтому раньше реальный hostname не подхватывался
        /// вообще — сервер так и висел в панели под дефолтным именем.
        /// </summary>
        internal string GetEffectiveServerName()
        {
            var configured = _config?.ServerName;
            var isDefault = string.IsNullOrWhiteSpace(configured)
                            || configured == "My Rust Server";

            if (!isDefault) return configured;

            var hostname = ConVar.Server.hostname;
            return string.IsNullOrWhiteSpace(hostname) ? "Rust Server" : hostname;
        }

        internal string GetPlayerIp(BasePlayer player)
        {
            return player?.net?.connection?.ipaddress?.Split(':')[0] ?? "";
        }

        #endregion

        #region Configuration

        internal const string PLUGIN_VERSION = "1.0.6";

        internal PluginConfig _config;

        internal class PluginConfig
        {
            [JsonProperty("Server Name")]
            public string ServerName { get; set; } = "My Rust Server";

            [JsonProperty("Avatar SteamID")]
            public string AvatarSteamId { get; set; } = "76561198726485072";

            [JsonProperty("Default Mute Duration (minutes)")]
            public int DefaultMuteDurationMinutes { get; set; } = 60;

            [JsonProperty("Max Warnings Before Mute")]
            public int MaxWarningsBeforeMute { get; set; } = 3;

            [JsonProperty("Auto Mute Duration On Warns (seconds)")]
            public int AutoMuteDurationSeconds { get; set; } = 36000;

            [JsonProperty("Appeal Service Name")]
            public string AppealServiceName { get; set; } = "";

            [JsonProperty("Appeal Service URL")]
            public string AppealServiceUrl { get; set; } = "";

            [JsonProperty("Map Update Interval (seconds)")]
            public float MapUpdateInterval { get; set; } = 5f;

            [JsonProperty("Clan Enabled")]
            public bool ClanEnabled { get; set; } = false;

            [JsonProperty("Panel URL")]
            public string PanelUrl { get; set; } = "https://overpanel.ru";

            [JsonProperty("Modules")]
            public ModulesConfig Modules { get; set; } = new ModulesConfig();
        }

        internal class ModulesConfig
        {
            [JsonProperty("Bans")]
            public bool Bans { get; set; } = true;
            [JsonProperty("Mutes")]
            public bool Mutes { get; set; } = true;
            [JsonProperty("Warns")]
            public bool Warns { get; set; } = true;
            [JsonProperty("Checks")]
            public bool Checks { get; set; } = true;
            [JsonProperty("Reports")]
            public bool Reports { get; set; } = true;
            [JsonProperty("AccessList")]
            public bool AccessList { get; set; } = true;
            [JsonProperty("Map")]
            public bool Map { get; set; } = true;
            [JsonProperty("Radio")]
            public bool Radio { get; set; } = false;
        }

        private void LoadConfig()
        {
            _config = Config.ReadObject<PluginConfig>() ?? new PluginConfig();
            SaveConfig();
        }

        protected override void LoadDefaultConfig()
        {
            _config = new PluginConfig();
        }

        protected override void SaveConfig()
        {
            Config.WriteObject(_config, true);
        }

        private void ApplyConfigUpdate(string key, object value)
        {
            switch (key)
            {
                case "server_name":        _config.ServerName = value?.ToString(); break;
                case "avatar_steamid":     _config.AvatarSteamId = value?.ToString(); break;
                case "default_mute_min":   if (int.TryParse(value?.ToString(), out var muteMin)) _config.DefaultMuteDurationMinutes = muteMin; break;
                case "max_warns":          if (int.TryParse(value?.ToString(), out var maxWarns)) _config.MaxWarningsBeforeMute = maxWarns; break;
                case "appeal_name":        _config.AppealServiceName = value?.ToString(); break;
                case "appeal_url":         _config.AppealServiceUrl = value?.ToString(); break;
            }
            SaveConfig();
        }

        /// <summary>Пришло из панели: config.update — применяем без перезагрузки.</summary>
        private void HandleActionConfigUpdate(JObject msg)
        {
            var settings = msg["settings"] as JObject;
            if (settings == null) return;

            foreach (var prop in settings.Properties())
                ApplyConfigUpdate(prop.Name, prop.Value?.ToObject<object>());

            Puts("[Overpanel] Конфигурация обновлена из панели без перезагрузки.");
        }

        #endregion

        #region Data & Storage

        private bool ValidateAudioFiles()
        {
            var ruPath = Path.Combine(Interface.Oxide.DataDirectory, "Overpanel", "audio", "check_ru.bin");
            var enPath = Path.Combine(Interface.Oxide.DataDirectory, "Overpanel", "audio", "check_en.bin");

            if (!File.Exists(ruPath) || !File.Exists(enPath))
            {
                PrintError("[Overpanel] REQUIRED: Audio files missing!");
                PrintError("[Overpanel] Place check_ru.bin and check_en.bin in oxide/data/Overpanel/audio/");
                PrintError("[Overpanel] Plugin will unload.");
                Interface.Oxide.UnloadPlugin(Name);
                return false;
            }
            return true;
        }

        private void CreateDirectoryStructure()
        {
            var baseDir = Path.Combine(Interface.Oxide.DataDirectory, "Overpanel");
            var dirs = new[] { "audio", "images", "logs", "Administrators", "YearlyRecap" };
            foreach (var dir in dirs)
                Directory.CreateDirectory(Path.Combine(baseDir, dir));
        }

        // ======= YearlyRecap Data =======

        private YearlyRecapData _currentRecap = new YearlyRecapData();

        internal class YearlyRecapData
        {
            public int Year              { get; set; }
            public int Bans              { get; set; }
            public int Mutes             { get; set; }
            public int Warns             { get; set; }
            public int Checks            { get; set; }
            public int ReportsProcessed  { get; set; }
            public int MultiaccountsDetected { get; set; }
            public int CheckerPositives  { get; set; }
            public int NewPlayers        { get; set; }
            public long TotalUptimeSeconds { get; set; }
            public int PeakOnlineCount   { get; set; }
            public DateTime? PeakOnlineDate { get; set; }
        }

        private void InitYearlyRecap()
        {
            _currentRecap = LoadYearlyRecap();
            _currentRecap.Year = DateTime.UtcNow.Year;
        }

        private YearlyRecapData LoadYearlyRecap()
        {
            try
            {
                return Interface.Oxide.DataFileSystem.ReadObject<YearlyRecapData>("Overpanel/YearlyRecap/current")
                       ?? new YearlyRecapData();
            }
            catch { return new YearlyRecapData(); }
        }

        private void SaveYearlyRecap()
        {
            Interface.Oxide.DataFileSystem.WriteObject("Overpanel/YearlyRecap/current", _currentRecap);
        }

        internal void IncrementRecap(string field)
        {
            switch (field)
            {
                case "bans":           _currentRecap.Bans++;           break;
                case "mutes":          _currentRecap.Mutes++;          break;
                case "warns":          _currentRecap.Warns++;          break;
                case "checks":         _currentRecap.Checks++;         break;
                case "reports":        _currentRecap.ReportsProcessed++; break;
                case "multiaccounts":  _currentRecap.MultiaccountsDetected++; break;
                case "checker_positive": _currentRecap.CheckerPositives++; break;
                case "new_players":    _currentRecap.NewPlayers++;     break;
            }
            SaveYearlyRecap();
        }

        private void FinalizeYearlyRecap()
        {
            var year = DateTime.UtcNow.Year;
            var archivePath = $"Overpanel/YearlyRecap/{year}";
            Interface.Oxide.DataFileSystem.WriteObject(archivePath, _currentRecap);

            _currentRecap = new YearlyRecapData { Year = year + 1 };
            SaveYearlyRecap();

            Puts($"[Overpanel] Yearly recap for {year} finalized.");
        }

        #endregion

        #region Connection & Pairing

        private const string CONNECTION_FILE = "Overpanel/Connection";

        /// <summary>
        /// op.pair &lt;КОД&gt; — привязка сервера к проекту в панели.
        /// Код одноразовый, генерируется в панели и живёт 10 минут.
        /// </summary>
        [ConsoleCommand("op.pair")]
        private void CmdPair(ConsoleSystem.Arg arg)
        {
            // Только серверная консоль или владелец — не даём привязать чужой сервер
            if (arg.Player() != null && !arg.Player().IsAdmin)
            {
                arg.ReplyWith("Команда доступна только из консоли сервера.");
                return;
            }

            if (arg.Args == null || arg.Args.Length < 1)
            {
                arg.ReplyWith("Использование: op.pair <КОД>\nКод генерируется в панели: Серверы → Добавить сервер");
                return;
            }

            // arg.Args — Facepunch.StringView[], не string[] (после обновления рантайма Rust).
            // arg.GetString(i) — правильный способ получить настоящий string.
            var code = arg.GetString(0).Trim().ToUpperInvariant();
            if (code.Length != 6)
            {
                arg.ReplyWith("Код должен состоять из 6 символов.");
                return;
            }

            var backendUrl = GetBackendUrl();
            var payload = JsonConvert.SerializeObject(new
            {
                code,
                name          = GetEffectiveServerName(),
                ip            = GetServerIp(),
                port          = ConVar.Server.port,
                pluginVersion = PLUGIN_VERSION,
            });

            arg.ReplyWith($"[Overpanel] Отправляю запрос на привязку...");

            webrequest.Enqueue(
                $"{backendUrl}/servers/pair",
                payload,
                (code2, response) => OnPairResponse(code2, response, backendUrl),
                this,
                RequestMethod.POST,
                new Dictionary<string, string> { ["Content-Type"] = "application/json" },
                15f);
        }

        private void OnPairResponse(int statusCode, string response, string backendUrl)
        {
            if (statusCode != 200 && statusCode != 201)
            {
                var reason = "неизвестная ошибка";
                try
                {
                    var err = JObject.Parse(response ?? "{}");
                    reason = err["error"]?.ToString() ?? reason;
                }
                catch { /* тело не JSON */ }

                PrintError($"[Overpanel] Привязка не удалась (HTTP {statusCode}): {reason}");
                return;
            }

            try
            {
                var json     = JObject.Parse(response);
                var token    = json["server_token"]?.ToString();
                var serverId = json["serverId"]?.ToString();

                if (string.IsNullOrEmpty(token))
                {
                    PrintError("[Overpanel] Панель не вернула server_token.");
                    return;
                }

                SaveConnection(token, backendUrl, serverId);

                _serverToken     = token;
                _backendUrl      = backendUrl;
                _currentServerId = serverId;

                Puts($"[Overpanel] Сервер успешно привязан! ID: {serverId}");
                Puts("[Overpanel] Устанавливаю соединение с панелью...");

                _shuttingDown     = false;
                _reconnectAttempt = 0;
                ConnectWebSocket();
            }
            catch (Exception ex)
            {
                PrintError($"[Overpanel] Ошибка разбора ответа привязки: {ex.Message}");
            }
        }

        /// <summary>op.status — диагностика соединения.</summary>
        [ConsoleCommand("op.status")]
        private void CmdStatus(ConsoleSystem.Arg arg)
        {
            var lines = new List<string>
            {
                $"Overpanel v{PLUGIN_VERSION} ({(_isCarbon ? "Carbon" : "Oxide/uMod")})",
                $"Привязан:     {(!string.IsNullOrEmpty(_serverToken) ? "да" : "нет — выполните op.pair <КОД>")}",
                $"Backend:      {_backendUrl ?? "не задан"}",
                $"Server ID:    {_currentServerId ?? "—"}",
                $"WS-статус:    {(IsBackendConnected ? "подключено" : "отключено")}",
                $"В очереди:    {_outboundQueue.Count} сообщений",
                $"Админов:      {_adminsCache.Count}",
                $"Проверок:     {_checkSessions.Count} активных",
            };

            arg.ReplyWith(string.Join("\n", lines));
        }

        /// <summary>op.reconnect — принудительное переподключение.</summary>
        [ConsoleCommand("op.reconnect")]
        private void CmdReconnect(ConsoleSystem.Arg arg)
        {
            if (string.IsNullOrEmpty(_serverToken))
            {
                arg.ReplyWith("Сервер не привязан. Выполните op.pair <КОД>");
                return;
            }

            _shuttingDown     = false;
            _reconnectAttempt = 0;
            _reconnectTimer?.Destroy();
            ConnectWebSocket();

            arg.ReplyWith("[Overpanel] Переподключение запущено.");
        }

        /// <summary>op.unpair — отвязать сервер от панели.</summary>
        [ConsoleCommand("op.unpair")]
        private void CmdUnpair(ConsoleSystem.Arg arg)
        {
            if (arg.Player() != null)
            {
                arg.ReplyWith("Команда доступна только из консоли сервера.");
                return;
            }

            ShutdownWebSocket();
            Interface.Oxide.DataFileSystem.WriteObject(CONNECTION_FILE, new Dictionary<string, string>());

            _serverToken     = null;
            _currentServerId = null;
            _wsAuthed        = false;

            arg.ReplyWith("[Overpanel] Сервер отвязан от панели.");
        }

        // ── Хранение параметров подключения ──────────────────────────

        private void SaveConnection(string token, string backendUrl, string serverId)
        {
            Interface.Oxide.DataFileSystem.WriteObject(CONNECTION_FILE, new Dictionary<string, string>
            {
                ["server_token"] = token,
                ["backend_url"]  = backendUrl,
                ["server_id"]    = serverId ?? "",
            });
        }

        private string GetServerToken()
        {
            var data = ReadConnectionFile();
            return data.TryGetValue("server_token", out var token) ? token : "";
        }

        private string GetBackendUrl()
        {
            var data = ReadConnectionFile();
            if (data.TryGetValue("backend_url", out var url) && !string.IsNullOrEmpty(url))
                return url;

            return "https://api.overpanel.ru";
        }

        private Dictionary<string, string> ReadConnectionFile()
        {
            try
            {
                return Interface.Oxide.DataFileSystem.ReadObject<Dictionary<string, string>>(CONNECTION_FILE)
                       ?? new Dictionary<string, string>();
            }
            catch
            {
                return new Dictionary<string, string>();
            }
        }

        private string GetServerIp()
        {
            var ip = ConVar.Server.ip;
            if (!string.IsNullOrEmpty(ip) && ip != "0.0.0.0")
                return ip;

            // Публичный IP определит сама панель по источнику запроса
            return "";
        }

        #endregion

        #region WebSocket

        private WebSocket _ws;
        private Timer _reconnectTimer;
        private Timer _queueFlushTimer;

        private string _serverToken;
        private string _backendUrl;
        internal string _currentProjectId;

        private bool _wsAuthed;
        private int  _reconnectAttempt;
        private bool _shuttingDown;

        private readonly Queue<string> _outboundQueue = new Queue<string>();
        private readonly object _queueLock = new object();

        private const int MAX_QUEUE_SIZE      = 500;
        private const int BASE_BACKOFF_SEC    = 2;
        private const int MAX_BACKOFF_SEC     = 120;

        internal bool IsBackendConnected => _wsAuthed && _ws != null && _ws.ReadyState == WebSocketState.Open;

        // ── Подключение ──────────────────────────────────────────────

        private void InitWebSocket()
        {
            _serverToken = GetServerToken();
            _backendUrl  = GetBackendUrl();

            if (string.IsNullOrEmpty(_serverToken))
            {
                PrintWarning("[Overpanel] Сервер не привязан к панели. Выполните: op.pair <КОД>");
                return;
            }

            ConnectWebSocket();

            // Очередь разгружается, даже если соединение временно упало
            _queueFlushTimer = timer.Every(1f, FlushOutboundQueue);
        }

        private void ConnectWebSocket()
        {
            if (_shuttingDown) return;

            try
            {
                CloseSocket();

                var wsUrl = BuildWebSocketUrl();
                _ws = new WebSocket(wsUrl);
                _ws.WaitTime = TimeSpan.FromSeconds(10);

                _ws.OnOpen    += OnWsOpen;
                _ws.OnMessage += OnWsMessage;
                _ws.OnError   += OnWsError;
                _ws.OnClose   += OnWsClose;

                _ws.ConnectAsync();
            }
            catch (Exception ex)
            {
                PrintError($"[Overpanel] Не удалось создать WS-соединение: {ex.Message}");
                ScheduleReconnect();
            }
        }

        private string BuildWebSocketUrl()
        {
            var baseUrl = _backendUrl.TrimEnd('/');

            if (baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                baseUrl = "wss://" + baseUrl.Substring(8);
            else if (baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                baseUrl = "ws://" + baseUrl.Substring(7);

            return $"{baseUrl}/ws/plugin";
        }

        private void OnWsOpen(object sender, EventArgs e)
        {
            // Хуки WebSocketSharp приходят не в главном потоке Unity
            NextTick(() =>
            {
                _reconnectAttempt = 0;
                SendRaw(new Dictionary<string, object>
                {
                    ["type"]         = "auth",
                    ["server_token"] = _serverToken,
                    ["version"]      = PLUGIN_VERSION,
                    ["framework"]    = _isCarbon ? "carbon" : "oxide",
                });
            });
        }

        private void OnWsMessage(object sender, MessageEventArgs e)
        {
            if (!e.IsText) return;

            NextTick(() =>
            {
                try
                {
                    var msg = JObject.Parse(e.Data);
                    DispatchAction(msg);
                }
                catch (Exception ex)
                {
                    PrintError($"[Overpanel] Ошибка разбора сообщения: {ex.Message}");
                }
            });
        }

        private void OnWsError(object sender, WebSocketSharp.ErrorEventArgs e)
        {
            NextTick(() => PrintWarning($"[Overpanel] WS ошибка: {e.Message}"));
        }

        private void OnWsClose(object sender, CloseEventArgs e)
        {
            NextTick(() =>
            {
                _wsAuthed = false;

                if (_shuttingDown) return;

                // 4003 — неверный токен, реконнект не поможет
                if (e.Code == 4003)
                {
                    PrintError($"[Overpanel] Панель отклонила server_token. Выполните op.pair заново.");
                    return;
                }

                PrintWarning($"[Overpanel] Соединение с панелью закрыто ({e.Code}). Переподключение...");
                ScheduleReconnect();
            });
        }

        private void ScheduleReconnect()
        {
            if (_shuttingDown) return;

            _reconnectTimer?.Destroy();

            // Экспоненциальный backoff: 2, 4, 8, 16 ... но не более 120 сек
            var delay = Math.Min(BASE_BACKOFF_SEC * Math.Pow(2, _reconnectAttempt), MAX_BACKOFF_SEC);
            _reconnectAttempt++;

            _reconnectTimer = timer.Once((float)delay, ConnectWebSocket);
        }

        private void CloseSocket()
        {
            if (_ws == null) return;

            try
            {
                _ws.OnOpen    -= OnWsOpen;
                _ws.OnMessage -= OnWsMessage;
                _ws.OnError   -= OnWsError;
                _ws.OnClose   -= OnWsClose;

                if (_ws.ReadyState == WebSocketState.Open)
                    _ws.Close();
            }
            catch { /* сокет уже мёртв */ }

            _ws = null;
        }

        private void ShutdownWebSocket()
        {
            _shuttingDown = true;
            _reconnectTimer?.Destroy();
            _queueFlushTimer?.Destroy();
            CloseSocket();
        }

        // ── Отправка ─────────────────────────────────────────────────

        /// <summary>
        /// Отправляет событие в панель. Если связи нет — кладёт в очередь,
        /// которая разгрузится после переподключения.
        /// </summary>
        internal void SendEvent(string eventName, Dictionary<string, object> data, string requestId = null)
        {
            var envelope = new Dictionary<string, object>
            {
                ["type"]  = "event",
                ["event"] = eventName,
                ["data"]  = data ?? new Dictionary<string, object>(),
            };

            if (!string.IsNullOrEmpty(requestId))
                envelope["request_id"] = requestId;

            SendRaw(envelope);
        }

        private void SendRaw(Dictionary<string, object> envelope)
        {
            string json;
            try
            {
                json = JsonConvert.SerializeObject(envelope);
            }
            catch (Exception ex)
            {
                PrintError($"[Overpanel] Не удалось сериализовать сообщение: {ex.Message}");
                return;
            }

            if (_ws != null && _ws.ReadyState == WebSocketState.Open)
            {
                try
                {
                    _ws.Send(json);
                    return;
                }
                catch { /* упадёт в очередь ниже */ }
            }

            EnqueueOutbound(json);
        }

        private void EnqueueOutbound(string json)
        {
            lock (_queueLock)
            {
                // Позиции игроков и статистика устаревают — старое выбрасываем
                if (_outboundQueue.Count >= MAX_QUEUE_SIZE)
                    _outboundQueue.Dequeue();

                _outboundQueue.Enqueue(json);
            }
        }

        private void FlushOutboundQueue()
        {
            if (_ws == null || _ws.ReadyState != WebSocketState.Open || !_wsAuthed)
                return;

            lock (_queueLock)
            {
                var batch = 0;
                while (_outboundQueue.Count > 0 && batch < 50)
                {
                    var json = _outboundQueue.Peek();
                    try
                    {
                        _ws.Send(json);
                        _outboundQueue.Dequeue();
                        batch++;
                    }
                    catch
                    {
                        break; // связь снова упала, дожидаемся реконнекта
                    }
                }
            }
        }

        // ── Приём команд ─────────────────────────────────────────────
        //
        // Все action-обработчики (HandleAction*) живут в тематических регионах
        // ниже (Punishments, Checks, Access List, Permissions, Reports, ...) —
        // это единственная точка диспетчеризации команд, пришедших из панели.

        private void DispatchAction(JObject msg)
        {
            var action = msg["action"]?.ToString();
            if (string.IsNullOrEmpty(action)) return;

            var requestId = msg["request_id"]?.ToString();

            switch (action)
            {
                case "auth.ok":
                    _wsAuthed = true;
                    _currentServerId = msg["server_id"]?.ToString();
                    _currentProjectId = msg["project_id"]?.ToString();
                    Puts($"[Overpanel] Подключено к панели. Сервер: {_currentServerId}");
                    OnBackendReady();
                    break;

                case "auth.failed":
                    PrintError($"[Overpanel] Аутентификация отклонена: {msg["reason"]}");
                    break;

                case "ping":
                    SendRaw(new Dictionary<string, object> { ["type"] = "pong" });
                    break;

                case "punishment.ban":    HandleActionBan(msg);        break;
                case "punishment.mute":   HandleActionMute(msg);       break;
                case "punishment.warn":   HandleActionWarn(msg);       break;
                case "punishment.revoke": HandleActionRevoke(msg);     break;

                case "check.start":       HandleActionCheckStart(msg); break;
                case "check.stop":        HandleActionCheckStop(msg);  break;

                case "chat.send":         HandleActionChatSend(msg);   break;

                case "rcon.exec":         HandleActionRconExec(msg, requestId); break;

                case "restart.start":     HandleActionRestartStart(msg);  break;
                case "restart.cancel":    HandleActionRestartCancel(msg); break;

                case "accesslist.update": HandleActionAccessListUpdate(msg); break;
                case "config.update":     HandleActionConfigUpdate(msg);     break;
                case "roles.update":      HandleActionRolesUpdate(msg);      break;

                case "report.message":       HandleActionReportMessage(msg); break;
                case "report.close":         HandleActionReportClose(msg);   break;
                case "report.registered":    HandleActionReportRegistered(msg); break;
                case "report.list_response": HandleActionReportListResponse(msg); break;

                case "player.teleport":   HandleActionTeleport(msg); break;

                default:
                    PrintWarning($"[Overpanel] Неизвестное действие: {action}");
                    break;
            }
        }

        /// <summary>Первичная синхронизация после успешной авторизации.</summary>
        private void OnBackendReady()
        {
            SendServerStats();

            foreach (var name in GetDetectedIntegrations())
            {
                SendEvent("integration.detected", new Dictionary<string, object>
                {
                    ["plugin_name"] = name,
                });
            }

            // Игроки, которые были в онлайне до реконнекта
            foreach (var player in BasePlayer.activePlayerList)
            {
                SendEvent("player.connected", new Dictionary<string, object>
                {
                    ["steamid"] = player.UserIDString,
                    ["name"]    = player.displayName,
                    ["ip"]      = GetPlayerIp(player),
                    ["team_id"] = player.currentTeam,
                });
            }
        }

        #endregion

        #region Permissions & Admins

        // steamId → admin data
        private Dictionary<string, AdminData> _adminsCache = new Dictionary<string, AdminData>();

        internal class AdminData
        {
            public string SteamId    { get; set; }
            public string Name       { get; set; }
            public int    Level      { get; set; } = 0;
            public string Title      { get; set; } = "Тестер";
            public string RoleId     { get; set; }
            public HashSet<string> Permissions { get; set; } = new HashSet<string>();
            public int Mutes         { get; set; }
            public int Warns         { get; set; }
            public int Bans          { get; set; }
            public int Checks        { get; set; }
            public DateTime LastAction { get; set; }
        }

        private void InitPermissionsCache()
        {
            var dataDir = Interface.Oxide.DataFileSystem.GetFile("Overpanel/Administrators");
            // Administrators/ files are loaded when available
            // Full sync happens via WebSocket after connection
        }

        internal bool HasPermission(string steamId, string permission)
        {
            if (!_adminsCache.TryGetValue(steamId, out var admin)) return false;
            if (admin.Level >= 9) return true;
            return admin.Permissions.Contains(permission);
        }

        internal int GetAdminLevel(string steamId)
        {
            return _adminsCache.TryGetValue(steamId, out var admin) ? admin.Level : -1;
        }

        internal bool CanInteract(string adminSteamId, string targetSteamId)
        {
            int adminLevel = GetAdminLevel(adminSteamId);
            int targetLevel = GetAdminLevel(targetSteamId);
            // Admin can interact if target is not an admin, or admin level > target level
            return targetLevel < 0 || adminLevel > targetLevel;
        }

        internal void UpdateAdminFromWs(AdminData data)
        {
            _adminsCache[data.SteamId] = data;
            AssignCarbonGroup(data.SteamId, data.Level);
            SaveAdminFile(data);
        }

        private void AssignCarbonGroup(string steamId, int level)
        {
            var player = BasePlayer.Find(steamId);
            if (player == null) return;

            // Clear existing groups
            permission.RemoveUserGroup(steamId, "moderatorid");
            permission.RemoveUserGroup(steamId, "ownerid");

            if (_isCarbon)
                Server.Command($"c.permgroup remove {steamId} developerid");

            if (level == 0) return;

            if (level >= 1 && level <= 6)
                permission.AddUserGroup(steamId, "moderatorid");
            else if (level >= 7 && level <= 8)
                permission.AddUserGroup(steamId, "ownerid");
            else if (level == 9)
            {
                permission.AddUserGroup(steamId, "ownerid");
                if (_isCarbon)
                    Server.Command($"c.permgroup add {steamId} developerid");
            }
        }

        private void SaveAdminFile(AdminData data)
        {
            try
            {
                var path = $"Overpanel/Administrators/{data.SteamId}";
                Interface.Oxide.DataFileSystem.WriteObject(path, data);
            }
            catch (Exception ex)
            {
                PrintError($"[Overpanel] Failed to save admin file: {ex.Message}");
            }
        }

        internal void IncrementAdminStat(string steamId, string stat)
        {
            if (!_adminsCache.TryGetValue(steamId, out var admin)) return;
            switch (stat)
            {
                case "mutes":  admin.Mutes++;  break;
                case "warns":  admin.Warns++;  break;
                case "bans":   admin.Bans++;   break;
                case "checks": admin.Checks++; break;
            }
            admin.LastAction = DateTime.UtcNow;
            SaveAdminFile(admin);
        }

        /// <summary>Пришло из панели: roles.update — синхронизация ролей/прав администраторов.</summary>
        private void HandleActionRolesUpdate(JObject msg)
        {
            var admins = msg["admins"] as JArray;
            if (admins == null) return;

            foreach (var entry in admins.OfType<JObject>())
            {
                var steamId = entry["steamid"]?.ToString();
                if (string.IsNullOrEmpty(steamId)) continue;

                var data = new AdminData
                {
                    SteamId     = steamId,
                    Name        = entry["name"]?.ToString() ?? steamId,
                    Level       = entry["level"]?.ToObject<int>() ?? 0,
                    Title       = entry["title"]?.ToString() ?? "Тестер",
                    RoleId      = entry["role_id"]?.ToString(),
                    Permissions = new HashSet<string>(
                        entry["permissions"]?.ToObject<List<string>>() ?? new List<string>()),
                };

                UpdateAdminFromWs(data);
            }

            Puts($"[Overpanel] Права обновлены: {admins.Count} администраторов.");
        }

        #endregion

        #region Access List

        // AccessList: mode "whitelist" | "blacklist"
        internal class AccessListState
        {
            public string Mode { get; set; } = "none"; // none | whitelist | blacklist
            public HashSet<string> SteamIds { get; set; } = new HashSet<string>();
        }

        // Один экземпляр плагина обслуживает ровно один сервер, поэтому состояние
        // всегда лежит под одним ключом. Раньше панель писала его под ключ serverId,
        // а CanUserLogin читал "local" — проверка входа смотрела на устаревшее
        // состояние из файла, и добавленный в whitelist игрок всё равно получал кик.
        private const string ACCESS_LIST_KEY = "local";

        private Dictionary<string, AccessListState> _accessListByServer = new Dictionary<string, AccessListState>();

        private AccessListState GetAccessListState()
        {
            if (!_accessListByServer.TryGetValue(ACCESS_LIST_KEY, out var state))
            {
                state = new AccessListState();
                _accessListByServer[ACCESS_LIST_KEY] = state;
            }
            return state;
        }

        // Schedules
        private List<AccessScheduleEntry> _schedules = new List<AccessScheduleEntry>();
        private Timer _scheduleTimer;

        internal class AccessScheduleEntry
        {
            public string ServerId { get; set; }
            public int[] Days      { get; set; }
            public string Start    { get; set; }
            public string End      { get; set; }
        }

        private string _currentServerId;

        private void InitAccessListScheduler()
        {
            _scheduleTimer = timer.Every(60f, () => CheckSchedules());
            LoadAccessListFromBackend();
        }

        /// <summary>
        /// Локальный кэш на случай, если панель ещё не прислала accesslist.update
        /// (сервер поднялся раньше бэкенда). Как только придёт синхронизация,
        /// состояние перезапишется актуальным.
        /// </summary>
        private void LoadAccessListFromBackend()
        {
            try
            {
                var state = Interface.Oxide.DataFileSystem.ReadObject<AccessListState>("Overpanel/accesslist");
                if (state != null)
                    _accessListByServer[ACCESS_LIST_KEY] = state;
            }
            catch { /* файла ещё нет — работаем с пустым состоянием */ }
        }

        // Раньше расписание писало прямо в state.Mode: это (а) включало whitelist
        // с пустым SteamIds, если реальный режим с панели был "none" без записей —
        // кикало вообще всех, и (б) при выходе из окна безусловно сбрасывало
        // state.Mode в "none", затирая настоящий whitelist/blacklist с панели,
        // если он случайно совпал со значением "whitelist". Теперь расписание —
        // отдельный флаг поверх состояния с панели, а не замена ему: оно может
        // только ДОБАВИТЬ whitelist-ограничение по уже существующим записям и
        // никогда не трогает Mode/SteamIds, синхронизированные с бэкенда.
        private bool _scheduleWhitelistWindow = false;

        private void CheckSchedules()
        {
            var now = DateTime.Now;
            int dayOfWeek = (int)now.DayOfWeek;
            string timeStr = now.ToString("HH:mm");

            bool inAnyWindow = false;
            foreach (var schedule in _schedules)
            {
                if (!schedule.Days.Contains(dayOfWeek)) continue;

                bool inWindow = string.CompareOrdinal(timeStr, schedule.Start) >= 0 &&
                                string.CompareOrdinal(timeStr, schedule.End) <= 0;

                if (inWindow)
                {
                    inAnyWindow = true;
                    break;
                }
            }

            if (inAnyWindow != _scheduleWhitelistWindow)
            {
                _scheduleWhitelistWindow = inAnyWindow;
                Puts(_scheduleWhitelistWindow
                    ? "[Overpanel][AccessList] По расписанию включён whitelist"
                    : "[Overpanel][AccessList] По расписанию whitelist выключен");
            }
        }

        object CanUserLogin(string name, string id, string address)
        {
            if (!_config.Modules.AccessList) return null;

            var state = GetAccessListState();

            // Расписание только усиливает ограничение по уже существующему списку —
            // пустой список по расписанию никого не кикает, а не форсит закрытие сервера
            bool whitelistActive = state.Mode == "whitelist"
                || (_scheduleWhitelistWindow && state.SteamIds.Count > 0);

            if (whitelistActive && !state.SteamIds.Contains(id))
                return "Сервер закрыт. Вас нет в whitelist.";

            if (state.Mode == "blacklist" && state.SteamIds.Contains(id))
                return "Вы заблокированы для входа на этот сервер.";

            return null;
        }

        internal void UpdateAccessList(string serverId, string mode, List<string> steamIds)
        {
            var state = new AccessListState
            {
                Mode = mode,
                SteamIds = new HashSet<string>(steamIds),
            };
            _accessListByServer[ACCESS_LIST_KEY] = state;

            Interface.Oxide.DataFileSystem.WriteObject("Overpanel/accesslist", state);
            Puts($"[Overpanel][AccessList] Обновлён: режим={mode}, записей={steamIds.Count}");
        }

        internal void AddToAccessList(string serverId, string steamId, string mode)
        {
            var state = GetAccessListState();
            state.SteamIds.Add(steamId);
            state.Mode = mode;
            Interface.Oxide.DataFileSystem.WriteObject("Overpanel/accesslist", state);
        }

        internal void RemoveFromAccessList(string serverId, string steamId)
        {
            var state = GetAccessListState();
            state.SteamIds.Remove(steamId);
            Interface.Oxide.DataFileSystem.WriteObject("Overpanel/accesslist", state);
        }

        /// <summary>Пришло из панели: accesslist.update — режим доступа и расписания.</summary>
        private void HandleActionAccessListUpdate(JObject msg)
        {
            var mode     = msg["mode"]?.ToString() ?? "none";
            var steamIds = msg["steamids"]?.ToObject<List<string>>() ?? new List<string>();
            var serverId = msg["server_id"]?.ToString() ?? _currentServerId;

            UpdateAccessList(serverId, mode, steamIds);

            var schedules = msg["schedules"]?.ToObject<List<AccessScheduleEntry>>();
            if (schedules != null)
            {
                _schedules.Clear();
                _schedules.AddRange(schedules);
            }
        }

        #endregion

        #region Punishments
        //
        // Единая система наказаний: Apply*/ApplyXRemote — точки входа
        // (от админа в игре либо от панели по SteamID), Execute* — фактическое
        // применение (бан/мут/варн игрока, синхронизация со сторонними
        // плагинами, событие в панель). HandleAction* ниже — это WS-обработчики,
        // которые просто разбирают сообщение от панели и вызывают тот же самый
        // ApplyXRemote/Execute*, что и команды администратора в игре — второй,
        // параллельной системы наказаний нет.

        private Dictionary<ulong, MuteData> _mutedPlayers = new Dictionary<ulong, MuteData>();
        private Dictionary<ulong, List<WarnEntry>> _playerWarns = new Dictionary<ulong, List<WarnEntry>>();

        internal class MuteData
        {
            public string Reason    { get; set; }
            public DateTime Expires { get; set; }
            public HashSet<string> Channels { get; set; } = new HashSet<string> { "Global" };
        }

        internal class WarnEntry
        {
            public string Reason     { get; set; }
            public string AdminTitle { get; set; }
            public DateTime IssuedAt { get; set; }
        }

        // ── Бан ──────────────────────────────────────────────────────

        /// <summary>Бан от администратора, находящегося в игре.</summary>
        private void ApplyBan(BasePlayer target, BasePlayer admin, string reason, int duration = 0)
        {
            if (!CanInteract(admin.UserIDString, target.UserIDString))
            {
                SendReply(admin, "[Overpanel] Вы не можете наказать администратора равного или выше по уровню.");
                return;
            }

            var adminTitle = GetAdminTitle(admin.UserIDString, admin.displayName);
            ExecuteBan(target.UserIDString, target.displayName, admin.UserIDString, adminTitle, reason, duration);
        }

        /// <summary>Бан по команде из панели — администратор может быть не в игре.</summary>
        internal void ApplyBanRemote(string targetSteamId, string adminSteamId, string adminTitle, string reason, int duration)
        {
            var target = BasePlayer.Find(targetSteamId);
            var targetName = target?.displayName ?? targetSteamId;

            ExecuteBan(targetSteamId, targetName, adminSteamId, adminTitle, reason, duration);
        }

        private void ExecuteBan(string targetSteamId, string targetName, string adminSteamId,
                                string adminTitle, string reason, int duration)
        {
            if (_isCarbon)
                Server.Command($"ban {targetSteamId} \"{reason}\" {duration}");
            else
                Server.Command($"banid {targetSteamId} \"{reason}\" {duration}");

            var target = BasePlayer.Find(targetSteamId);
            if (target != null)
                SyncBanToIQBanSystem(target, reason, duration);

            IncrementAdminStat(adminSteamId, "bans");
            IncrementRecap("bans");

            ChatAlert($"<color=#FF4444>БАН:</color> {targetName} заблокирован администратором " +
                      $"<color=#66ff66>{adminTitle}</color>. Причина: {reason}" +
                      (duration > 0 ? $" ({FormatDuration(duration)})" : " (навсегда)"));

            SendEvent("punishment.issued", new Dictionary<string, object>
            {
                ["type"]           = "ban",
                ["target_steamid"] = targetSteamId,
                ["target_name"]    = targetName,
                ["admin_steamid"]  = adminSteamId,
                ["admin_title"]    = adminTitle,
                ["reason"]         = reason,
                ["duration"]       = duration,
            });
        }

        // ── Мут ──────────────────────────────────────────────────────

        private void ApplyMute(BasePlayer target, BasePlayer admin, string reason, int durationSeconds, HashSet<string> channels)
        {
            if (!CanInteract(admin.UserIDString, target.UserIDString))
            {
                SendReply(admin, "[Overpanel] Нельзя замутить администратора равного или выше.");
                return;
            }

            var adminTitle = GetAdminTitle(admin.UserIDString, admin.displayName);
            ExecuteMute(target.UserIDString, target.displayName, admin.UserIDString, adminTitle,
                        reason, durationSeconds, channels);
        }

        internal void ApplyMuteRemote(string targetSteamId, string adminTitle, string reason,
                                      int durationSeconds, HashSet<string> channels)
        {
            var target = BasePlayer.Find(targetSteamId);
            var targetName = target?.displayName ?? targetSteamId;

            ExecuteMute(targetSteamId, targetName, null, adminTitle, reason, durationSeconds, channels);
        }

        private void ExecuteMute(string targetSteamId, string targetName, string adminSteamId,
                                 string adminTitle, string reason, int durationSeconds, HashSet<string> channels)
        {
            if (!ulong.TryParse(targetSteamId, out var uid)) return;

            var muteData = new MuteData
            {
                Reason   = reason,
                Expires  = durationSeconds > 0 ? DateTime.UtcNow.AddSeconds(durationSeconds) : DateTime.MaxValue,
                Channels = channels ?? new HashSet<string> { "Global" },
            };

            _mutedPlayers[uid] = muteData;

            var target = BasePlayer.Find(targetSteamId);
            if (target != null)
            {
                if (HasIntegration("IQChat"))
                    SyncMuteToIQChat(target, muteData);

                SendReply(target, $"[Overpanel] Вы замучены [{string.Join("/", muteData.Channels)}]. Причина: {reason}");
            }

            if (!string.IsNullOrEmpty(adminSteamId))
                IncrementAdminStat(adminSteamId, "mutes");
            IncrementRecap("mutes");

            ChatAlert($"<color=#FFAA00>МУТ:</color> {targetName} замучен администратором " +
                      $"<color=#66ff66>{adminTitle}</color>. Причина: {reason}" +
                      (durationSeconds > 0 ? $" ({FormatDuration(durationSeconds)})" : " (навсегда)"));

            SendEvent("punishment.issued", new Dictionary<string, object>
            {
                ["type"]           = "mute",
                ["target_steamid"] = targetSteamId,
                ["target_name"]    = targetName,
                ["admin_steamid"]  = adminSteamId,
                ["admin_title"]    = adminTitle,
                ["reason"]         = reason,
                ["duration"]       = durationSeconds,
                ["channels"]       = muteData.Channels.ToList(),
            });
        }

        internal bool IsPlayerMuted(BasePlayer player, string channel)
        {
            if (!_mutedPlayers.TryGetValue(player.userID, out var mute)) return false;

            if (mute.Expires < DateTime.UtcNow)
            {
                _mutedPlayers.Remove(player.userID);
                return false;
            }

            return mute.Channels.Contains(channel) || mute.Channels.Contains("Global");
        }

        // ── Предупреждения ───────────────────────────────────────────

        private void ApplyWarn(BasePlayer target, BasePlayer admin, string reason)
        {
            if (!CanInteract(admin.UserIDString, target.UserIDString))
            {
                SendReply(admin, "[Overpanel] Нельзя предупредить администратора равного или выше.");
                return;
            }

            var adminTitle = GetAdminTitle(admin.UserIDString, admin.displayName);
            ExecuteWarn(target.UserIDString, target.displayName, admin.UserIDString, adminTitle, reason);
        }

        internal void ApplyWarnRemote(string targetSteamId, string adminTitle, string reason)
        {
            var target = BasePlayer.Find(targetSteamId);
            var targetName = target?.displayName ?? targetSteamId;

            ExecuteWarn(targetSteamId, targetName, null, adminTitle, reason);
        }

        private void ExecuteWarn(string targetSteamId, string targetName, string adminSteamId,
                                 string adminTitle, string reason)
        {
            if (!ulong.TryParse(targetSteamId, out var uid)) return;

            if (!_playerWarns.ContainsKey(uid))
                _playerWarns[uid] = new List<WarnEntry>();

            _playerWarns[uid].Add(new WarnEntry
            {
                Reason     = reason,
                AdminTitle = adminTitle,
                IssuedAt   = DateTime.UtcNow,
            });

            var warnCount = _playerWarns[uid].Count;

            var target = BasePlayer.Find(targetSteamId);
            if (target != null)
            {
                SendReply(target, $"[Overpanel] Вы получили предупреждение " +
                                  $"({warnCount}/{_config.MaxWarningsBeforeMute}). Причина: {reason}");
            }

            if (!string.IsNullOrEmpty(adminSteamId))
                IncrementAdminStat(adminSteamId, "warns");
            IncrementRecap("warns");

            SendEvent("punishment.issued", new Dictionary<string, object>
            {
                ["type"]           = "warn",
                ["target_steamid"] = targetSteamId,
                ["target_name"]    = targetName,
                ["admin_steamid"]  = adminSteamId,
                ["admin_title"]    = adminTitle,
                ["reason"]         = reason,
            });

            // Порог предупреждений — автоматический мут и сброс счётчика
            if (warnCount >= _config.MaxWarningsBeforeMute)
            {
                _playerWarns.Remove(uid);

                ExecuteMute(targetSteamId, targetName, null, "Система",
                    $"Автоматический мут: {_config.MaxWarningsBeforeMute} предупреждения",
                    _config.AutoMuteDurationSeconds,
                    new HashSet<string> { "Global" });
            }
        }

        // ── Хук чата (применение мута) ───────────────────────────────

        object OnPlayerChat(BasePlayer player, string message, ConVar.Chat.ChatChannel channel)
        {
            var channelName = channel == ConVar.Chat.ChatChannel.Team ? "Team"
                            : channel == ConVar.Chat.ChatChannel.Clan ? "Clan"
                            : "Global";

            if (_config.Modules.Mutes && IsPlayerMuted(player, channelName))
            {
                if (_mutedPlayers.TryGetValue(player.userID, out var mute))
                {
                    var secsLeft = mute.Expires == DateTime.MaxValue
                        ? 0
                        : (int)(mute.Expires - DateTime.UtcNow).TotalSeconds;

                    var timeStr = secsLeft > 0 ? $" ({FormatDuration(secsLeft)})" : " (навсегда)";
                    SendReply(player, $"[Overpanel] Вы замучены в [{channelName}]. Причина: {mute.Reason}{timeStr}");
                }
                return false;
            }

            SendEvent("player.chat", new Dictionary<string, object>
            {
                ["steamid"] = player.UserIDString,
                ["name"]    = player.displayName,
                ["message"] = message,
                ["channel"] = channelName,
            });

            return null;
        }

        // ── WS-обработчики (панель → наказание) ──────────────────────

        private void HandleActionBan(JObject msg)
        {
            var targetId   = msg["target_steamid"]?.ToString();
            var adminId    = msg["admin_steamid"]?.ToString();
            var adminTitle = msg["admin_title"]?.ToString() ?? "Администратор";
            var reason     = msg["reason"]?.ToString() ?? "Не указана";
            var duration   = msg["duration"]?.ToObject<int>() ?? 0;

            if (string.IsNullOrEmpty(targetId)) return;

            ApplyBanRemote(targetId, adminId, adminTitle, reason, duration);
        }

        private void HandleActionMute(JObject msg)
        {
            var targetId   = msg["target_steamid"]?.ToString();
            var adminTitle = msg["admin_title"]?.ToString() ?? "Администратор";
            var reason     = msg["reason"]?.ToString() ?? "Не указана";
            var duration   = msg["duration"]?.ToObject<int>() ?? 0;

            var channels = msg["channels"]?.ToObject<List<string>>() ?? new List<string> { "Global" };

            if (string.IsNullOrEmpty(targetId)) return;

            ApplyMuteRemote(targetId, adminTitle, reason, duration, new HashSet<string>(channels));
        }

        private void HandleActionWarn(JObject msg)
        {
            var targetId   = msg["target_steamid"]?.ToString();
            var adminTitle = msg["admin_title"]?.ToString() ?? "Администратор";
            var reason     = msg["reason"]?.ToString() ?? "Не указана";

            if (string.IsNullOrEmpty(targetId)) return;

            ApplyWarnRemote(targetId, adminTitle, reason);
        }

        /// <summary>Снятие наказания (панель → сервер). Часть единой системы наказаний,
        /// отдельного механизма revoke не существует.</summary>
        private void HandleActionRevoke(JObject msg)
        {
            var targetId = msg["target_steamid"]?.ToString();
            var type     = msg["punishment_type"]?.ToString();

            if (string.IsNullOrEmpty(targetId)) return;

            if (type == "mute" && ulong.TryParse(targetId, out var uid))
            {
                _mutedPlayers.Remove(uid);
                var player = BasePlayer.FindByID(uid);
                if (player != null)
                    SendReply(player, "[Overpanel] С вас снят мут.");
            }
            else if (type == "ban")
            {
                Server.Command($"unban {targetId}");
            }
            else if (type == "warn" && ulong.TryParse(targetId, out var wuid))
            {
                _playerWarns.Remove(wuid);
            }
        }

        #endregion

        #region Checks

        // steamId цели → сессия
        private Dictionary<string, CheckSession> _checkSessions = new Dictionary<string, CheckSession>();

        internal class CheckSession
        {
            public string TargetSteamId { get; set; }
            public string AdminSteamId  { get; set; }
            public string AdminTitle    { get; set; }
            public string SessionId     { get; set; }
            public DateTime StartedAt   { get; set; }
            public bool   CheckerConnected { get; set; }
            public Timer  ConnectTimer  { get; set; }
        }

        private const int CHECK_CONNECT_TIMEOUT_SEC = 300;

        // ── Запуск из панели ─────────────────────────────────────────

        /// <summary>
        /// Проверка, инициированная из веб-панели. Админ может быть не в игре,
        /// поэтому работаем только по его SteamID.
        /// </summary>
        internal void StartCheckRemote(string sessionId, string targetSteamId, string adminSteamId, string adminTitle)
        {
            var target = BasePlayer.Find(targetSteamId);
            if (target == null || !target.IsConnected)
            {
                SendEvent("check.status", new Dictionary<string, object>
                {
                    ["session_id"] = sessionId,
                    ["steamid"]    = targetSteamId,
                    ["status"]     = "failed",
                    ["detail"]     = "Игрок не в сети",
                });
                return;
            }

            if (_checkSessions.ContainsKey(targetSteamId))
            {
                SendEvent("check.status", new Dictionary<string, object>
                {
                    ["session_id"] = sessionId,
                    ["steamid"]    = targetSteamId,
                    ["status"]     = "failed",
                    ["detail"]     = "Игрок уже проходит проверку",
                });
                return;
            }

            BeginCheck(sessionId, target, adminSteamId, adminTitle);
        }

        /// <summary>Проверка, начатая администратором в игре.</summary>
        internal void StartCheck(BasePlayer admin, BasePlayer target)
        {
            if (!_config.Modules.Checks)
            {
                SendReply(admin, "[Overpanel] Проверки отключены на этом сервере.");
                return;
            }

            if (!CanInteract(admin.UserIDString, target.UserIDString))
            {
                SendReply(admin, "[Overpanel] Нельзя проверить администратора равного или выше.");
                return;
            }

            if (_checkSessions.ContainsKey(target.UserIDString))
            {
                SendReply(admin, "[Overpanel] Этот игрок уже проходит проверку.");
                return;
            }

            var sessionId  = Guid.NewGuid().ToString("N").Substring(0, 16);
            var adminTitle = GetAdminTitle(admin.UserIDString, admin.displayName);

            BeginCheck(sessionId, target, admin.UserIDString, adminTitle);

            SendReply(admin, $"[Overpanel] Проверка начата для <color=#ff6600>{target.displayName}</color>.");
        }

        // ── Общая логика ─────────────────────────────────────────────

        private void BeginCheck(string sessionId, BasePlayer target, string adminSteamId, string adminTitle)
        {
            var session = new CheckSession
            {
                TargetSteamId = target.UserIDString,
                AdminSteamId  = adminSteamId,
                AdminTitle    = adminTitle,
                SessionId     = sessionId,
                StartedAt     = DateTime.UtcNow,
            };

            session.ConnectTimer = timer.Once(CHECK_CONNECT_TIMEOUT_SEC, () =>
            {
                if (!_checkSessions.ContainsKey(target.UserIDString)) return;
                if (session.CheckerConnected) return;

                BanForCheckFailure(target, adminSteamId, adminTitle,
                    "Не запустил Overpanel Checker вовремя");
                EndCheck(target.UserIDString);
            });

            _checkSessions[target.UserIDString] = session;

            // Оверлей и голос запускаются вместе: игрок видит текст и слышит диктора
            ShowCheckOverlay(target);
            PlayCheckVoice(target, sessionId);

            IncrementAdminStat(adminSteamId, "checks");
            IncrementRecap("checks");

            SendEvent("check.status", new Dictionary<string, object>
            {
                ["session_id"] = sessionId,
                ["steamid"]    = target.UserIDString,
                ["status"]     = "started",
                ["detail"]     = $"Оверлей показан, инициатор: {adminTitle}",
            });
        }

        internal void EndCheck(string targetSteamId)
        {
            if (!_checkSessions.TryGetValue(targetSteamId, out var session)) return;

            session.ConnectTimer?.Destroy();
            _checkSessions.Remove(targetSteamId);

            StopVoiceStream(targetSteamId);

            var target = BasePlayer.Find(targetSteamId);
            if (target != null)
                HideCheckOverlay(target);

            SendEvent("check.status", new Dictionary<string, object>
            {
                ["session_id"] = session.SessionId,
                ["steamid"]    = targetSteamId,
                ["status"]     = "completed",
            });
        }

        internal void MarkCheckerConnected(string targetSteamId)
        {
            if (_checkSessions.TryGetValue(targetSteamId, out var session))
                session.CheckerConnected = true;
        }

        private void BanForCheckFailure(BasePlayer target, string adminSteamId, string adminTitle, string reason)
        {
            if (target == null) return;

            ApplyBanRemote(target.UserIDString, adminSteamId, adminTitle, reason, 0);
        }

        // ── Реакция на выход игрока ──────────────────────────────────

        object OnUserDisconnected(Oxide.Core.Libraries.Covalence.IPlayer player, string reason)
        {
            if (_checkSessions.TryGetValue(player.Id, out var session))
            {
                var target = BasePlayer.Find(player.Id);
                if (target != null)
                {
                    ApplyBanRemote(player.Id, session.AdminSteamId, session.AdminTitle,
                        "Покинул сервер во время проверки", 0);
                }

                StopVoiceStream(player.Id);
                session.ConnectTimer?.Destroy();
                _checkSessions.Remove(player.Id);

                SendEvent("check.status", new Dictionary<string, object>
                {
                    ["session_id"] = session.SessionId,
                    ["steamid"]    = player.Id,
                    ["status"]     = "failed",
                    ["detail"]     = "Игрок покинул сервер",
                });
            }

            SendEvent("player.disconnected", new Dictionary<string, object>
            {
                ["steamid"] = player.Id,
                ["reason"]  = reason,
            });

            return null;
        }

        // ── WS-обработчики (панель → проверка) ───────────────────────

        private void HandleActionCheckStart(JObject msg)
        {
            var sessionId  = msg["session_id"]?.ToString();
            var targetId   = msg["target_steamid"]?.ToString();
            var adminId    = msg["admin_steamid"]?.ToString();
            var adminTitle = msg["admin_title"]?.ToString() ?? "Администратор";

            if (string.IsNullOrEmpty(targetId) || string.IsNullOrEmpty(sessionId)) return;

            StartCheckRemote(sessionId, targetId, adminId, adminTitle);
        }

        private void HandleActionCheckStop(JObject msg)
        {
            var targetId = msg["target_steamid"]?.ToString();
            if (string.IsNullOrEmpty(targetId)) return;

            EndCheck(targetId);
        }

        #endregion

        #region Audio

        /// <summary>
        /// Голосовое уведомление о проверке.
        ///
        /// Клиент Rust воспроизводит голос только от сущности игрока, которая
        /// ему видна. Поэтому позади цели спавнится невидимый для остальных
        /// фейковый игрок, и Opus-фреймы шлются пакетами VoiceData от его имени
        /// с интервалом 20 мс — ровно так, как их шлёт настоящий клиент.
        /// </summary>

        private const float FRAME_INTERVAL   = 0.02f;   // 20 мс — размер Opus-фрейма
        private const float FAKE_SPAWN_DIST  = 1.2f;    // на сколько метров позади цели
        private const float FAKE_SPAWN_UP    = 0.5f;
        private const int   MAX_FRAME_SIZE   = 2048;

        // Кеш распарсенных фреймов: путь → фреймы. Читаем файл один раз.
        private readonly Dictionary<string, byte[][]> _audioCache = new Dictionary<string, byte[][]>();

        // Активные стримы: steamId цели → корутина
        private readonly Dictionary<string, Coroutine> _activeStreams = new Dictionary<string, Coroutine>();
        private readonly Dictionary<string, BasePlayer> _fakePlayers = new Dictionary<string, BasePlayer>();

        // ── Чтение файла фреймов ─────────────────────────────────────

        /// <summary>
        /// Формат .bin: последовательность [int32 length][length байт Opus-фрейма].
        /// Порядок байт — little-endian (как пишет BinaryWriter).
        /// </summary>
        private byte[][] ReadOpusFrames(string relativePath)
        {
            if (_audioCache.TryGetValue(relativePath, out var cached))
                return cached;

            var fullPath = Path.Combine(Interface.Oxide.DataDirectory, "Overpanel", relativePath);

            if (!File.Exists(fullPath))
            {
                PrintError($"[Overpanel] Аудиофайл не найден: {fullPath}");
                return null;
            }

            try
            {
                var frames = new List<byte[]>();

                using (var stream = File.OpenRead(fullPath))
                using (var reader = new BinaryReader(stream))
                {
                    while (stream.Position < stream.Length)
                    {
                        // Оборванный хвост файла — читать нечего
                        if (stream.Length - stream.Position < sizeof(int)) break;

                        var length = reader.ReadInt32();

                        if (length <= 0 || length > MAX_FRAME_SIZE)
                        {
                            PrintError($"[Overpanel] Повреждённый фрейм в {relativePath}: длина {length}");
                            return null;
                        }

                        if (stream.Length - stream.Position < length) break;

                        frames.Add(reader.ReadBytes(length));
                    }
                }

                if (frames.Count == 0)
                {
                    PrintError($"[Overpanel] Файл {relativePath} не содержит фреймов.");
                    return null;
                }

                var result = frames.ToArray();
                _audioCache[relativePath] = result;

                Puts($"[Overpanel] Загружено {result.Length} Opus-фреймов из {relativePath} " +
                     $"(~{result.Length * FRAME_INTERVAL:F1} сек)");

                return result;
            }
            catch (Exception ex)
            {
                PrintError($"[Overpanel] Ошибка чтения {relativePath}: {ex.Message}");
                return null;
            }
        }

        // ── Публичный вход ───────────────────────────────────────────

        /// <summary>
        /// Проигрывает голосовое уведомление о проверке.
        /// Вызывается сразу после показа CUI-оверлея, чтобы игрок
        /// одновременно видел текст и слышал голос.
        /// </summary>
        internal void PlayCheckVoice(BasePlayer target, string sessionId)
        {
            if (target == null || !target.IsConnected) return;

            var lang  = GetPlayerLanguage(target) == "en" ? "en" : "ru";
            var frames = ReadOpusFrames($"audio/check_{lang}.bin");

            if (frames == null)
            {
                // Голос не критичен — оверлей уже показан, продолжаем текстом
                SendCheckTextFallback(target, lang);
                return;
            }

            StopVoiceStream(target.UserIDString);

            var fake = SpawnVoiceCarrier(target);
            if (fake == null)
            {
                SendCheckTextFallback(target, lang);
                return;
            }

            _fakePlayers[target.UserIDString] = fake;

            var coroutine = ServerMgr.Instance.StartCoroutine(
                StreamVoiceFrames(target, fake, frames, sessionId));

            _activeStreams[target.UserIDString] = coroutine;
        }

        internal void StopVoiceStream(string steamId)
        {
            if (_activeStreams.TryGetValue(steamId, out var coroutine))
            {
                if (coroutine != null)
                    ServerMgr.Instance.StopCoroutine(coroutine);
                _activeStreams.Remove(steamId);
            }

            if (_fakePlayers.TryGetValue(steamId, out var fake))
            {
                KillVoiceCarrier(fake);
                _fakePlayers.Remove(steamId);
            }
        }

        // ── Фейковый носитель голоса ─────────────────────────────────

        private BasePlayer SpawnVoiceCarrier(BasePlayer target)
        {
            try
            {
                // Клиент воспроизводит VoiceData только от сущности, которую он
                // уже получил по сети, поэтому носитель обязан быть заспавнен
                // и отправлен цели. Ставим его вплотную за спину, чтобы игрок
                // не увидел модель в поле зрения, и убиваем сразу после стрима.
                var forward = target.eyes != null
                    ? target.eyes.BodyForward()
                    : target.transform.forward;

                var behind = target.transform.position
                             - forward * FAKE_SPAWN_DIST
                             + Vector3.up * FAKE_SPAWN_UP;

                var entity = GameManager.server.CreateEntity(
                    "assets/prefabs/player/player.prefab",
                    behind,
                    Quaternion.LookRotation(forward));

                var fake = entity as BasePlayer;
                if (fake == null)
                {
                    entity?.Kill();
                    return null;
                }

                fake.Spawn();

                fake.displayName = "";
                fake.SendNetworkUpdateImmediate();

                return fake;
            }
            catch (Exception ex)
            {
                PrintError($"[Overpanel] Не удалось создать носитель голоса: {ex.Message}");
                return null;
            }
        }

        private void KillVoiceCarrier(BasePlayer fake)
        {
            if (fake == null || fake.IsDestroyed) return;

            try
            {
                fake.Kill();
            }
            catch (Exception ex)
            {
                PrintWarning($"[Overpanel] Ошибка удаления носителя голоса: {ex.Message}");
            }
        }

        // ── Стриминг ─────────────────────────────────────────────────

        private IEnumerator StreamVoiceFrames(
            BasePlayer target,
            BasePlayer carrier,
            byte[][] frames,
            string sessionId)
        {
            var wait = new WaitForSeconds(FRAME_INTERVAL);
            var sentFrames = 0;

            for (var i = 0; i < frames.Length; i++)
            {
                // Игрок вышел или проверка отменена — прерываем
                if (target == null || !target.IsConnected) break;
                if (carrier == null || carrier.IsDestroyed) break;

                SendVoiceFrame(target, carrier, frames[i]);
                sentFrames++;

                yield return wait;
            }

            KillVoiceCarrier(carrier);
            _fakePlayers.Remove(target?.UserIDString ?? "");
            _activeStreams.Remove(target?.UserIDString ?? "");

            var completed = sentFrames == frames.Length;

            SendEvent("check.status", new Dictionary<string, object>
            {
                ["session_id"] = sessionId,
                ["steamid"]    = target?.UserIDString,
                ["status"]     = completed ? "voice_played" : "voice_interrupted",
                ["detail"]     = $"Отправлено {sentFrames} из {frames.Length} фреймов",
            });
        }

        /// <summary>
        /// Формирует пакет VoiceData так же, как это делает настоящий клиент,
        /// и шлёт его только целевому игроку.
        /// </summary>
        private void SendVoiceFrame(BasePlayer target, BasePlayer carrier, byte[] frame)
        {
            if (target.net?.connection == null) return;

            try
            {
                var writer = Net.sv.StartWrite();
                writer.PacketID(Message.Type.VoiceData);
                writer.EntityID(carrier.net.ID);
                writer.BytesWithSize(frame);
                writer.Send(new SendInfo(target.net.connection));
            }
            catch (Exception ex)
            {
                PrintWarning($"[Overpanel] Ошибка отправки голосового фрейма: {ex.Message}");
            }
        }

        private void SendCheckTextFallback(BasePlayer player, string lang)
        {
            var msg = lang == "en"
                ? "[Overpanel] You have been called for a check! Launch Overpanel Checker."
                : "[Overpanel] Вы вызваны на проверку! Запустите Overpanel Checker.";

            SendReply(player, msg);
        }

        // ── Очистка ──────────────────────────────────────────────────

        private void ShutdownAudio()
        {
            foreach (var steamId in new List<string>(_activeStreams.Keys))
                StopVoiceStream(steamId);

            _activeStreams.Clear();
            _fakePlayers.Clear();
            _audioCache.Clear();
        }

        #endregion

        #region CUI Overlays

        // ======= CHECK CUI =======

        private const string CHECK_PANEL_UI  = "overpanel.check";
        private const string RESTART_PANEL_UI = "overpanel.restart";
        private const string RULES_PANEL_UI   = "overpanel.rules";
        private const string REPORT_LIST_PANEL_UI   = "overpanel.report.list";
        private const string REPORT_DETAIL_PANEL_UI = "overpanel.report.detail";

        private Dictionary<ulong, bool> _activeChecks = new Dictionary<ulong, bool>();

        internal void ShowCheckOverlay(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, CHECK_PANEL_UI);

            bool isRu = GetPlayerLanguage(player) == "ru";
            string title    = isRu ? "ПРОВЕРКА" : "CHECK";
            string line1    = isRu ? "К вам применена проверка." : "You are being checked.";
            string line2    = isRu ? "Не покидайте сервер и запустите Overpanel Checker." : "Do not leave the server and launch Overpanel Checker.";
            string discord  = isRu ? "Введите /discord ВашДискорд" : "Type /discord YourDiscord";

            var elements = new CuiElementContainer();

            var panel = elements.Add(new CuiPanel
            {
                Image = { Color = "0 0 0 0.85", Material = "assets/content/ui/uibackgroundblur.mat" },
                RectTransform = { AnchorMin = "0.25 0.6", AnchorMax = "0.75 0.85" },
                CursorEnabled = false
            }, "Overlay", CHECK_PANEL_UI);

            elements.Add(new CuiLabel
            {
                Text = { Text = title, FontSize = 20, Align = TextAnchor.MiddleCenter, Color = "1 0.2 0.2 1" },
                RectTransform = { AnchorMin = "0 0.7", AnchorMax = "1 1" }
            }, panel);

            elements.Add(new CuiLabel
            {
                Text = { Text = line1, FontSize = 14, Align = TextAnchor.MiddleCenter, Color = "1 1 1 0.9" },
                RectTransform = { AnchorMin = "0 0.45", AnchorMax = "1 0.7" }
            }, panel);

            elements.Add(new CuiLabel
            {
                Text = { Text = line2, FontSize = 12, Align = TextAnchor.MiddleCenter, Color = "0.8 0.8 0.8 0.8" },
                RectTransform = { AnchorMin = "0 0.2", AnchorMax = "1 0.45" }
            }, panel);

            elements.Add(new CuiLabel
            {
                Text = { Text = discord, FontSize = 11, Align = TextAnchor.MiddleCenter, Color = "0.6 1 0.6 0.9" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 0.2" }
            }, panel);

            CuiHelper.AddUi(player, elements);
            _activeChecks[player.userID] = true;
        }

        internal void HideCheckOverlay(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, CHECK_PANEL_UI);
            _activeChecks.Remove(player.userID);
        }

        // ======= RESTART CUI =======

        private int _restartSecondsLeft;
        private string _restartReason;
        private string _restartInitiator;
        private Timer _restartCountdownTimer;

        internal void ShowRestartCountdown(int seconds, string reason, string initiator)
        {
            _restartSecondsLeft = seconds;
            _restartReason = reason;
            _restartInitiator = initiator;

            foreach (var player in BasePlayer.activePlayerList)
                UpdateRestartOverlay(player);

            _restartCountdownTimer = timer.Every(1f, () =>
            {
                _restartSecondsLeft--;
                if (_restartSecondsLeft <= 0)
                {
                    _restartCountdownTimer?.Destroy();
                    ExecuteRestart();
                    return;
                }
                foreach (var p in BasePlayer.activePlayerList)
                    UpdateRestartOverlay(p);
            });
        }

        internal void CancelRestart()
        {
            _restartCountdownTimer?.Destroy();
            foreach (var player in BasePlayer.activePlayerList)
                CuiHelper.DestroyUi(player, RESTART_PANEL_UI);
        }

        private void UpdateRestartOverlay(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, RESTART_PANEL_UI);

            var elements = new CuiElementContainer();
            var panel = elements.Add(new CuiPanel
            {
                Image = { Color = "0.08 0.08 0.08 0.9" },
                RectTransform = { AnchorMin = "0.3 0.94", AnchorMax = "0.7 1.0" }
            }, "Overlay", RESTART_PANEL_UI);

            var text = $"ПЕРЕЗАПУСК СЕРВЕРА — {_restartSecondsLeft} сек. | {_restartReason}";
            elements.Add(new CuiLabel
            {
                Text = { Text = text, FontSize = 13, Align = TextAnchor.MiddleCenter, Color = "1 0.5 0 1" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" }
            }, panel);

            CuiHelper.AddUi(player, elements);
        }

        private void ExecuteRestart()
        {
            foreach (var player in BasePlayer.activePlayerList)
                CuiHelper.DestroyUi(player, RESTART_PANEL_UI);

            if (_isCarbon)
                Server.Command("c.restart");
            else
                Server.Command("restart");
        }

        // ======= RULES CUI =======

        private Dictionary<ulong, string> _playerRulesCache = new Dictionary<ulong, string>();

        internal void ShowRulesScreen(BasePlayer player, string rulesText)
        {
            CuiHelper.DestroyUi(player, RULES_PANEL_UI);

            var elements = new CuiElementContainer();
            var panel = elements.Add(new CuiPanel
            {
                Image = { Color = "0.06 0.06 0.06 0.97" },
                RectTransform = { AnchorMin = "0.2 0.1", AnchorMax = "0.8 0.9" },
                CursorEnabled = true
            }, "Overlay", RULES_PANEL_UI);

            elements.Add(new CuiLabel
            {
                Text = { Text = "ПРАВИЛА СЕРВЕРА", FontSize = 18, Align = TextAnchor.UpperCenter, Color = "0.6 1 0.6 1" },
                RectTransform = { AnchorMin = "0 0.92", AnchorMax = "1 1" }
            }, panel);

            elements.Add(new CuiLabel
            {
                Text = { Text = string.IsNullOrEmpty(rulesText) ? "Правила не установлены." : rulesText,
                         FontSize = 12, Align = TextAnchor.UpperLeft, Color = "0.9 0.9 0.9 1" },
                RectTransform = { AnchorMin = "0.03 0.05", AnchorMax = "0.97 0.9" }
            }, panel);

            elements.Add(new CuiButton
            {
                Button = { Command = "overpanel.closerules", Color = "0.3 0.3 0.3 1" },
                RectTransform = { AnchorMin = "0.4 0.01", AnchorMax = "0.6 0.05" },
                Text = { Text = "Закрыть", FontSize = 12, Align = TextAnchor.MiddleCenter }
            }, panel);

            CuiHelper.AddUi(player, elements);
        }

        [ConsoleCommand("overpanel.closerules")]
        private void CmdCloseRules(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player != null)
                CuiHelper.DestroyUi(player, RULES_PANEL_UI);
        }

        // ======= REPORT CUI =======
        //
        // Двухколоночный интерфейс: слева список обращений, справа переписка.
        // Данные (ReportEntryData/кэш/запросы к панели) живут в регионе
        // "Reports & Player Commands" — здесь только построение экранов.

        private static readonly Dictionary<string, string> ReportStatusLabel = new Dictionary<string, string>
        {
            ["new"]         = "Открыт",
            ["in_progress"] = "В работе",
            ["closed"]      = "Закрыт",
        };

        // Палитра под макет
        private const string COL_BG        = "0.043 0.055 0.098 0.98";
        private const string COL_CARD      = "0.078 0.094 0.153 1";
        private const string COL_CARD_ALT  = "0.106 0.125 0.196 1";
        private const string COL_ACCENT    = "0.145 0.388 0.921 1";
        private const string COL_TEXT      = "0.91 0.93 0.98 1";
        private const string COL_MUTED     = "0.55 0.60 0.71 1";
        private const string COL_GREEN     = "0.20 0.83 0.60 1";
        private const string COL_AMBER     = "0.98 0.75 0.14 1";
        private const string COL_RED       = "0.94 0.27 0.27 1";

        private static string Rect(double v) => v.ToString("F4", CultureInfo.InvariantCulture);

        private string GetReportStatusColor(ReportEntryData r)
        {
            if (r.Status == "closed") return COL_MUTED;
            if (r.NeedsHelp) return COL_ACCENT;
            if (r.IsPriority) return COL_RED;
            if (r.Status == "in_progress") return COL_AMBER;
            return COL_GREEN;
        }

        private static string ShortDate(string iso)
        {
            return DateTime.TryParse(iso, null, DateTimeStyles.RoundtripKind, out var dt)
                ? dt.ToLocalTime().ToString("dd.MM HH:mm", CultureInfo.InvariantCulture)
                : "";
        }

        private static string TimeOnly(string iso)
        {
            return DateTime.TryParse(iso, null, DateTimeStyles.RoundtripKind, out var dt)
                ? dt.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)
                : "";
        }

        private static string DayOnly(string iso)
        {
            return DateTime.TryParse(iso, null, DateTimeStyles.RoundtripKind, out var dt)
                ? dt.ToLocalTime().ToString("d MMMM yyyy", CultureInfo.GetCultureInfo("ru-RU"))
                : "";
        }

        private static string SubjectOf(ReportEntryData r)
        {
            if (!string.IsNullOrEmpty(r.Subject)) return r.Subject;
            var first = r.Messages.FirstOrDefault(m => m.AuthorType == "player")?.Text ?? "";
            return first.Length > 42 ? first.Substring(0, 42) + "…" : first;
        }

        /// <summary>
        /// Корневая панель. Если в data/Overpanel/images/REPORT_SCREEN.png лежит
        /// картинка (1202×805), она рисуется фоном; иначе — сплошная заливка.
        /// </summary>
        private string AddReportRoot(CuiElementContainer elements, string uiName)
        {
            var panel = elements.Add(new CuiPanel
            {
                Image = { Color = COL_BG },
                RectTransform = { AnchorMin = "0.13 0.09", AnchorMax = "0.87 0.91" },
                CursorEnabled = true
            }, "Overlay", uiName);

            if (_reportBgCrc.HasValue)
            {
                elements.Add(new CuiElement
                {
                    Parent = panel,
                    Components =
                    {
                        new CuiRawImageComponent { Png = _reportBgCrc.Value.ToString(), Color = "1 1 1 1" },
                        new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "1 1" }
                    }
                });
            }

            return panel;
        }

        /// <summary>Общая шапка: заголовок, «Правила», крестик.</summary>
        private void AddReportHeader(CuiElementContainer elements, string parent)
        {
            elements.Add(new CuiPanel
            {
                Image = { Color = COL_ACCENT },
                RectTransform = { AnchorMin = "0.018 0.905", AnchorMax = "0.052 0.975" }
            }, parent);

            elements.Add(new CuiLabel
            {
                Text = { Text = "Система репортов", FontSize = 19, Align = TextAnchor.LowerLeft, Color = COL_TEXT },
                RectTransform = { AnchorMin = "0.065 0.938", AnchorMax = "0.6 0.982" }
            }, parent);

            elements.Add(new CuiLabel
            {
                Text = { Text = "Связь с администрацией сервера", FontSize = 11, Align = TextAnchor.UpperLeft, Color = COL_MUTED },
                RectTransform = { AnchorMin = "0.065 0.9", AnchorMax = "0.6 0.938" }
            }, parent);

            elements.Add(new CuiButton
            {
                Button = { Command = "overpanel.report.rules", Color = COL_CARD_ALT },
                RectTransform = { AnchorMin = "0.845 0.918", AnchorMax = "0.94 0.972" },
                Text = { Text = "? Правила", FontSize = 11, Align = TextAnchor.MiddleCenter, Color = COL_TEXT }
            }, parent);

            elements.Add(new CuiButton
            {
                Button = { Command = "overpanel.report.close", Color = "0 0 0 0" },
                RectTransform = { AnchorMin = "0.95 0.918", AnchorMax = "0.985 0.972" },
                Text = { Text = "✕", FontSize = 20, Align = TextAnchor.MiddleCenter, Color = COL_MUTED }
            }, parent);
        }

        /// <summary>Левая колонка: кнопка создания и список обращений по секциям.</summary>
        private void AddReportSidebar(CuiElementContainer elements, string parent, BasePlayer player, string activeId)
        {
            var reports = _reportListCache.TryGetValue(player.userID, out var list) ? list : new List<ReportEntryData>();
            var open   = reports.Where(r => r.Status != "closed").ToList();
            var closed = reports.Where(r => r.Status == "closed").ToList();

            var side = elements.Add(new CuiPanel
            {
                Image = { Color = COL_CARD },
                RectTransform = { AnchorMin = "0.018 0.02", AnchorMax = "0.325 0.885" }
            }, parent);

            elements.Add(new CuiButton
            {
                Button = { Command = "overpanel.report.new", Color = COL_ACCENT },
                RectTransform = { AnchorMin = "0.05 0.925", AnchorMax = "0.95 0.985" },
                Text = { Text = "+  Создать репорт", FontSize = 13, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, side);

            // Верстаем сверху вниз: заголовок секции, затем её карточки
            var y = 0.9;
            const double headerH = 0.045;
            const double cardH   = 0.105;
            const double gap     = 0.012;

            void Section(string title, int count)
            {
                elements.Add(new CuiLabel
                {
                    Text = { Text = title, FontSize = 11, Align = TextAnchor.MiddleLeft, Color = COL_MUTED },
                    RectTransform = { AnchorMin = $"0.06 {Rect(y - headerH)}", AnchorMax = $"0.75 {Rect(y)}" }
                }, side);

                elements.Add(new CuiLabel
                {
                    Text = { Text = count.ToString(), FontSize = 11, Align = TextAnchor.MiddleRight, Color = COL_MUTED },
                    RectTransform = { AnchorMin = $"0.75 {Rect(y - headerH)}", AnchorMax = $"0.94 {Rect(y)}" }
                }, side);

                y -= headerH + gap;
            }

            void Card(ReportEntryData r)
            {
                if (y - cardH < 0.02) return; // место кончилось

                var top = y;
                var bottom = y - cardH;
                var isActive = r.Id == activeId;

                var card = elements.Add(new CuiButton
                {
                    Button = { Command = $"overpanel.report.open {r.Id}", Color = isActive ? "0.114 0.208 0.373 1" : COL_CARD_ALT },
                    RectTransform = { AnchorMin = $"0.05 {Rect(bottom)}", AnchorMax = $"0.95 {Rect(top)}" },
                    Text = { Text = "" }
                }, side);

                elements.Add(new CuiLabel
                {
                    Text = { Text = $"#{r.Id}", FontSize = 12, Align = TextAnchor.MiddleLeft, Color = isActive ? COL_ACCENT : COL_TEXT },
                    RectTransform = { AnchorMin = "0.06 0.62", AnchorMax = "0.6 0.95" }
                }, card);

                elements.Add(new CuiLabel
                {
                    Text = { Text = ReportStatusLabel.TryGetValue(r.Status, out var st) ? st : r.Status,
                             FontSize = 9, Align = TextAnchor.MiddleRight, Color = GetReportStatusColor(r) },
                    RectTransform = { AnchorMin = "0.55 0.62", AnchorMax = "0.94 0.95" }
                }, card);

                elements.Add(new CuiLabel
                {
                    Text = { Text = SubjectOf(r), FontSize = 10, Align = TextAnchor.UpperLeft, Color = COL_MUTED },
                    RectTransform = { AnchorMin = "0.06 0.28", AnchorMax = "0.94 0.62" }
                }, card);

                elements.Add(new CuiLabel
                {
                    Text = { Text = (r.Status == "closed" ? "Закрыт: " : "Создан: ") + ShortDate(r.CreatedAt),
                             FontSize = 9, Align = TextAnchor.MiddleLeft, Color = COL_MUTED },
                    RectTransform = { AnchorMin = "0.06 0.04", AnchorMax = "0.94 0.28" }
                }, card);

                y = bottom - gap;
            }

            Section("Текущие обращения", open.Count);
            foreach (var r in open.Take(4)) Card(r);

            if (closed.Count > 0)
            {
                y -= gap;
                Section("Прошлые обращения", closed.Count);
                foreach (var r in closed.Take(3)) Card(r);
            }

            if (reports.Count == 0)
            {
                elements.Add(new CuiLabel
                {
                    Text = { Text = "Обращений пока нет", FontSize = 11, Align = TextAnchor.MiddleCenter, Color = COL_MUTED },
                    RectTransform = { AnchorMin = "0.05 0.7", AnchorMax = "0.95 0.85" }
                }, side);
            }
        }

        /// <summary>Правая колонка со списком, когда обращение не выбрано.</summary>
        internal void ShowReportListScreen(BasePlayer player)
        {
            ShowReportScreen(player, null);
        }

        internal void ShowReportDetailScreen(BasePlayer player, string reportId)
        {
            ShowReportScreen(player, reportId);
        }

        /// <summary>Единый экран: сайдбар + область справа (переписка либо подсказка).</summary>
        private void ShowReportScreen(BasePlayer player, string activeId)
        {
            CuiHelper.DestroyUi(player, REPORT_LIST_PANEL_UI);
            CuiHelper.DestroyUi(player, REPORT_DETAIL_PANEL_UI);

            var reports = _reportListCache.TryGetValue(player.userID, out var list) ? list : new List<ReportEntryData>();
            var report = activeId != null ? reports.FirstOrDefault(r => r.Id == activeId) : null;

            if (report != null) _openReportDetail[player.userID] = report.Id;
            else _openReportDetail.Remove(player.userID);

            var elements = new CuiElementContainer();
            var root = AddReportRoot(elements, REPORT_LIST_PANEL_UI);
            AddReportHeader(elements, root);
            AddReportSidebar(elements, root, player, report?.Id);

            var pane = elements.Add(new CuiPanel
            {
                Image = { Color = COL_CARD },
                RectTransform = { AnchorMin = "0.34 0.02", AnchorMax = "0.982 0.885" }
            }, root);

            if (report == null)
            {
                elements.Add(new CuiLabel
                {
                    Text = { Text = reports.Count == 0
                                ? "У вас ещё нет обращений.\nНажмите «Создать репорт», чтобы обратиться к администрации."
                                : "Выберите обращение слева, чтобы открыть переписку.",
                             FontSize = 13, Align = TextAnchor.MiddleCenter, Color = COL_MUTED },
                    RectTransform = { AnchorMin = "0.08 0.4", AnchorMax = "0.92 0.6" }
                }, pane);

                CuiHelper.AddUi(player, elements);
                return;
            }

            // ── шапка обращения ──
            elements.Add(new CuiLabel
            {
                Text = { Text = $"Репорт #{report.Id}", FontSize = 16, Align = TextAnchor.LowerLeft, Color = COL_TEXT },
                RectTransform = { AnchorMin = "0.025 0.925", AnchorMax = "0.45 0.985" }
            }, pane);

            elements.Add(new CuiLabel
            {
                Text = { Text = ReportStatusLabel.TryGetValue(report.Status, out var statusLabel) ? statusLabel : report.Status,
                         FontSize = 10, Align = TextAnchor.LowerLeft, Color = GetReportStatusColor(report) },
                RectTransform = { AnchorMin = "0.3 0.93", AnchorMax = "0.55 0.98" }
            }, pane);

            elements.Add(new CuiLabel
            {
                Text = { Text = SubjectOf(report), FontSize = 11, Align = TextAnchor.UpperLeft, Color = COL_MUTED },
                RectTransform = { AnchorMin = "0.025 0.885", AnchorMax = "0.7 0.93" }
            }, pane);

            if (report.Status != "closed")
            {
                elements.Add(new CuiButton
                {
                    Button = { Command = $"overpanel.report.resolve {report.Id}", Color = "0.35 0.11 0.11 1" },
                    RectTransform = { AnchorMin = "0.735 0.925", AnchorMax = "0.975 0.985" },
                    Text = { Text = "Закрыть обращение", FontSize = 11, Align = TextAnchor.MiddleCenter, Color = COL_RED }
                }, pane);
            }

            // ── лента сообщений ──
            const int maxMessages = 5;
            var shown = report.Messages.Count > maxMessages
                ? report.Messages.Skip(report.Messages.Count - maxMessages).ToList()
                : report.Messages;

            const double feedTop = 0.865;
            const double feedBottom = 0.135;

            if (shown.Count == 0)
            {
                elements.Add(new CuiLabel
                {
                    Text = { Text = "Сообщений пока нет.", FontSize = 12, Align = TextAnchor.MiddleCenter, Color = COL_MUTED },
                    RectTransform = { AnchorMin = $"0.05 {Rect(feedBottom)}", AnchorMax = $"0.95 {Rect(feedTop)}" }
                }, pane);
            }
            else
            {
                // Разделитель с датой первого показанного сообщения
                elements.Add(new CuiLabel
                {
                    Text = { Text = DayOnly(shown[0].CreatedAt), FontSize = 10, Align = TextAnchor.MiddleCenter, Color = COL_MUTED },
                    RectTransform = { AnchorMin = $"0.05 {Rect(feedTop - 0.045)}", AnchorMax = $"0.95 {Rect(feedTop)}" }
                }, pane);

                var areaTop = feedTop - 0.055;
                var rowH = (areaTop - feedBottom) / shown.Count;

                for (var i = 0; i < shown.Count; i++)
                {
                    var m = shown[i];
                    var top = areaTop - i * rowH;
                    var bottom = top - rowH + 0.008;
                    var isPlayer = m.AuthorType == "player";
                    var isSystem = m.AuthorType == "system";

                    var authorName = isPlayer ? "Вы" : isSystem ? "Система" : "Администратор";
                    var authorColor = isPlayer ? COL_TEXT : isSystem ? COL_ACCENT : COL_GREEN;

                    // Аватар-заглушка: кружок с первой буквой роли
                    var avatar = elements.Add(new CuiPanel
                    {
                        Image = { Color = isPlayer ? COL_CARD_ALT : isSystem ? "0.09 0.16 0.30 1" : "0.10 0.26 0.20 1" },
                        RectTransform = { AnchorMin = $"0.025 {Rect(bottom)}", AnchorMax = $"0.065 {Rect(top)}" }
                    }, pane);

                    elements.Add(new CuiLabel
                    {
                        Text = { Text = isPlayer ? "И" : isSystem ? "S" : "A", FontSize = 12,
                                 Align = TextAnchor.MiddleCenter, Color = authorColor },
                        RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" }
                    }, avatar);

                    var bubble = elements.Add(new CuiPanel
                    {
                        Image = { Color = COL_CARD_ALT },
                        RectTransform = { AnchorMin = $"0.075 {Rect(bottom)}", AnchorMax = $"0.975 {Rect(top)}" }
                    }, pane);

                    elements.Add(new CuiLabel
                    {
                        Text = { Text = $"{authorName}   {TimeOnly(m.CreatedAt)}", FontSize = 10,
                                 Align = TextAnchor.UpperLeft, Color = authorColor },
                        RectTransform = { AnchorMin = "0.015 0.62", AnchorMax = "0.98 0.97" }
                    }, bubble);

                    elements.Add(new CuiLabel
                    {
                        Text = { Text = m.Text, FontSize = 11, Align = TextAnchor.UpperLeft, Color = COL_TEXT },
                        RectTransform = { AnchorMin = "0.015 0.05", AnchorMax = "0.98 0.62" }
                    }, bubble);
                }
            }

            // ── строка ввода ──
            if (report.Status != "closed")
            {
                elements.Add(new CuiButton
                {
                    Button = { Command = "overpanel.report.attach", Color = COL_CARD_ALT },
                    RectTransform = { AnchorMin = "0.025 0.03", AnchorMax = "0.075 0.115" },
                    Text = { Text = "📎", FontSize = 14, Align = TextAnchor.MiddleCenter, Color = COL_MUTED }
                }, pane);

                var inputBg = elements.Add(new CuiPanel
                {
                    Image = { Color = COL_CARD_ALT },
                    RectTransform = { AnchorMin = "0.085 0.03", AnchorMax = "0.775 0.115" }
                }, pane);

                elements.Add(new CuiElement
                {
                    Parent = inputBg,
                    Components =
                    {
                        new CuiInputFieldComponent
                        {
                            Command = $"overpanel.report.send {report.Id}",
                            FontSize = 12,
                            Color = COL_TEXT,
                            CharsLimit = 400,
                            NeedsKeyboard = true,
                            Align = TextAnchor.MiddleLeft,
                            Text = "",
                        },
                        new CuiRectTransformComponent { AnchorMin = "0.015 0.05", AnchorMax = "0.985 0.95" }
                    }
                });

                elements.Add(new CuiButton
                {
                    Button = { Command = $"overpanel.report.urgent {report.Id}", Color = "0.35 0.15 0.15 1" },
                    RectTransform = { AnchorMin = "0.785 0.03", AnchorMax = "0.975 0.115" },
                    Text = { Text = "Проблема актуальна", FontSize = 11, Align = TextAnchor.MiddleCenter, Color = "1 0.85 0.85 1" }
                }, pane);
            }
            else
            {
                elements.Add(new CuiLabel
                {
                    Text = { Text = "Обращение закрыто. Напишите новое, если проблема повторилась.",
                             FontSize = 11, Align = TextAnchor.MiddleCenter, Color = COL_MUTED },
                    RectTransform = { AnchorMin = "0.025 0.03", AnchorMax = "0.975 0.115" }
                }, pane);
            }

            CuiHelper.AddUi(player, elements);
        }

        /// <summary>Экран выбора категории при создании обращения.</summary>
        private void ShowReportCreateScreen(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, REPORT_LIST_PANEL_UI);
            CuiHelper.DestroyUi(player, REPORT_DETAIL_PANEL_UI);

            var elements = new CuiElementContainer();
            var root = AddReportRoot(elements, REPORT_DETAIL_PANEL_UI);
            AddReportHeader(elements, root);

            var pane = elements.Add(new CuiPanel
            {
                Image = { Color = COL_CARD },
                RectTransform = { AnchorMin = "0.018 0.02", AnchorMax = "0.982 0.885" }
            }, root);

            elements.Add(new CuiLabel
            {
                Text = { Text = "Новое обращение — выберите категорию", FontSize = 15, Align = TextAnchor.MiddleLeft, Color = COL_TEXT },
                RectTransform = { AnchorMin = "0.03 0.9", AnchorMax = "0.7 0.97" }
            }, pane);

            elements.Add(new CuiButton
            {
                Button = { Command = "overpanel.report.back", Color = COL_CARD_ALT },
                RectTransform = { AnchorMin = "0.8 0.9", AnchorMax = "0.97 0.965" },
                Text = { Text = "< Назад", FontSize = 11, Align = TextAnchor.MiddleCenter, Color = COL_TEXT }
            }, pane);

            var y = 0.85;
            const double h = 0.085;
            for (var i = 0; i < ReportCategories.Length; i++)
            {
                elements.Add(new CuiButton
                {
                    // Индекс, а не текст: пробелы и скобки в аргументе консольной команды ломают разбор
                    Button = { Command = $"overpanel.report.category {i}", Color = COL_CARD_ALT },
                    RectTransform = { AnchorMin = $"0.03 {Rect(y - h)}", AnchorMax = $"0.62 {Rect(y)}" },
                    Text = { Text = ReportCategories[i], FontSize = 12, Align = TextAnchor.MiddleLeft, Color = COL_TEXT }
                }, pane);
                y -= h + 0.015;
            }

            elements.Add(new CuiLabel
            {
                Text = { Text = "После выбора категории опишите проблему одним сообщением.\n\n"
                              + "Доказательства: загрузите видео на Яндекс.Диск или Dropbox\n"
                              + "и вставьте ссылку прямо в текст сообщения.",
                         FontSize = 11, Align = TextAnchor.UpperLeft, Color = COL_MUTED },
                RectTransform = { AnchorMin = "0.65 0.45", AnchorMax = "0.97 0.85" }
            }, pane);

            CuiHelper.AddUi(player, elements);
        }

        /// <summary>Подсказка по вложениям (загрузить файл из игры нельзя).</summary>
        private void ShowReportAttachHelp(BasePlayer player)
        {
            const string ui = "overpanel.report.attachhelp";
            CuiHelper.DestroyUi(player, ui);

            var elements = new CuiElementContainer();
            var panel = elements.Add(new CuiPanel
            {
                Image = { Color = COL_CARD },
                RectTransform = { AnchorMin = "0.3 0.34", AnchorMax = "0.7 0.66" },
                CursorEnabled = true
            }, "Overlay", ui);

            elements.Add(new CuiLabel
            {
                Text = { Text = "Как приложить доказательства", FontSize = 15, Align = TextAnchor.MiddleCenter, Color = COL_TEXT },
                RectTransform = { AnchorMin = "0.05 0.82", AnchorMax = "0.95 0.95" }
            }, panel);

            elements.Add(new CuiLabel
            {
                Text = { Text = "Загрузить файл прямо из игры нельзя.\n\n"
                              + "1. Загрузите видео или скриншот на Яндекс.Диск либо Dropbox\n"
                              + "2. Откройте доступ по ссылке\n"
                              + "3. Вставьте ссылку в сообщение — она прикрепится к обращению\n\n"
                              + "Ссылку Dropbox панель сама переведёт в прямую загрузку.",
                         FontSize = 11, Align = TextAnchor.UpperLeft, Color = COL_MUTED },
                RectTransform = { AnchorMin = "0.07 0.22", AnchorMax = "0.93 0.8" }
            }, panel);

            elements.Add(new CuiButton
            {
                Button = { Command = "overpanel.report.attachclose", Color = COL_ACCENT },
                RectTransform = { AnchorMin = "0.35 0.06", AnchorMax = "0.65 0.18" },
                Text = { Text = "Понятно", FontSize = 12, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, panel);

            CuiHelper.AddUi(player, elements);
        }

        [ConsoleCommand("overpanel.report.open")]
        private void CmdReportOpen(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || arg.Args == null || arg.Args.Length < 1) return;
            ShowReportDetailScreen(player, arg.GetString(0));
        }

        [ConsoleCommand("overpanel.report.back")]
        private void CmdReportBack(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player != null) ShowReportListScreen(player);
        }

        [ConsoleCommand("overpanel.report.new")]
        private void CmdReportNew(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player != null) ShowReportCreateScreen(player);
        }

        [ConsoleCommand("overpanel.report.category")]
        private void CmdReportCategory(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || arg.Args == null || arg.Args.Length < 1) return;
            if (!int.TryParse(arg.GetString(0), out var index)) return;
            if (index < 0 || index >= ReportCategories.Length) return;

            _pendingReportCategory[player.userID] = ReportCategories[index];

            CuiHelper.DestroyUi(player, REPORT_LIST_PANEL_UI);
            CuiHelper.DestroyUi(player, REPORT_DETAIL_PANEL_UI);

            SendReply(player,
                $"<color=#5599FF>[Overpanel]</color> Категория: {ReportCategories[index]}.\n" +
                "Опишите проблему командой: /report <текст>. Ссылку на доказательства можно вставить прямо в текст.");
        }

        [ConsoleCommand("overpanel.report.rules")]
        private void CmdReportRulesFromCui(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;
            CuiHelper.DestroyUi(player, REPORT_LIST_PANEL_UI);
            CuiHelper.DestroyUi(player, REPORT_DETAIL_PANEL_UI);
            ShowRulesScreen(player, GetServerRules());
        }

        [ConsoleCommand("overpanel.report.attach")]
        private void CmdReportAttach(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player != null) ShowReportAttachHelp(player);
        }

        [ConsoleCommand("overpanel.report.attachclose")]
        private void CmdReportAttachClose(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player != null) CuiHelper.DestroyUi(player, "overpanel.report.attachhelp");
        }

        [ConsoleCommand("overpanel.report.close")]
        private void CmdReportCloseScreen(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;
            CuiHelper.DestroyUi(player, REPORT_LIST_PANEL_UI);
            CuiHelper.DestroyUi(player, REPORT_DETAIL_PANEL_UI);
            CuiHelper.DestroyUi(player, "overpanel.report.attachhelp");
            _openReportDetail.Remove(player.userID);
        }

        [ConsoleCommand("overpanel.report.resolve")]
        private void CmdReportResolve(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || arg.Args == null || arg.Args.Length < 1) return;
            MarkReportResolved(player, arg.GetString(0));
        }

        [ConsoleCommand("overpanel.report.urgent")]
        private void CmdReportUrgent(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || arg.Args == null || arg.Args.Length < 1) return;
            MarkReportUrgent(player, arg.GetString(0));
        }

        [ConsoleCommand("overpanel.report.send")]
        private void CmdReportSend(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || arg.Args == null || arg.Args.Length < 1) return;

            var reportId = arg.GetString(0);
            var words = new List<string>();
            for (var i = 1; i < arg.Args.Length; i++) words.Add(arg.GetString(i));
            var text = string.Join(" ", words);

            SendReportMessage(player, reportId, text);
        }

        // ======= CLEANUP =======

        private void CleanupAll()
        {
            foreach (var player in BasePlayer.activePlayerList)
            {
                CuiHelper.DestroyUi(player, CHECK_PANEL_UI);
                CuiHelper.DestroyUi(player, RESTART_PANEL_UI);
                CuiHelper.DestroyUi(player, REPORT_LIST_PANEL_UI);
                CuiHelper.DestroyUi(player, REPORT_DETAIL_PANEL_UI);
                CuiHelper.DestroyUi(player, "overpanel.report.attachhelp");
                CuiHelper.DestroyUi(player, RULES_PANEL_UI);
            }
            _restartCountdownTimer?.Destroy();
        }

        // ── WS-обработчики (панель → рестарт сервера) ────────────────

        private void HandleActionRestartStart(JObject msg)
        {
            var seconds   = msg["seconds"]?.ToObject<int>() ?? 300;
            var reason    = msg["reason"]?.ToString() ?? "Технические работы";
            var initiator = msg["admin_title"]?.ToString() ?? "Администрация";

            ShowRestartCountdown(seconds, reason, initiator);
        }

        private void HandleActionRestartCancel(JObject msg)
        {
            CancelRestart();
            Server.Broadcast("<color=#66ff66>[Overpanel]</color> Перезапуск сервера отменён.");
        }

        #endregion

        #region Reports & Player Commands

        private readonly Dictionary<string, int> _playerReportCounts = new Dictionary<string, int>();

        // Ссылки распознаём, чтобы отправить их отдельным полем attachments
        private static readonly string[] AttachmentHosts =
        {
            "disk.yandex.ru", "yadi.sk", "dropbox.com", "drive.google.com",
        };

        // ── Кэш /report CUI ──────────────────────────────────────────
        //
        // Плагин не хранит обращения у себя — список запрашивается у панели
        // (report.list_request → report.list_response, тот же принцип, что и
        // rcon.exec → rcon.output) и кэшируется на время сессии игрока.

        private class ReportMessageData
        {
            [JsonProperty("author_type")] public string AuthorType;
            [JsonProperty("text")] public string Text;
            [JsonProperty("created_at")] public string CreatedAt;
        }

        private class ReportEntryData
        {
            [JsonProperty("id")] public string Id;
            [JsonProperty("subject")] public string Subject;
            [JsonProperty("status")] public string Status;
            [JsonProperty("is_priority")] public bool IsPriority;
            [JsonProperty("needs_help")] public bool NeedsHelp;
            [JsonProperty("created_at")] public string CreatedAt;
            [JsonProperty("messages")] public List<ReportMessageData> Messages = new List<ReportMessageData>();
        }

        /// <summary>Категории для экрана «Создать репорт».</summary>
        private static readonly string[] ReportCategories =
        {
            "Читер (aim / подозрительная игра)",
            "Читер (ESP / видит сквозь стены)",
            "Оскорбления в чате",
            "Застрял в текстурах",
            "Баг / проблема сервера",
            "Другое",
        };

        private readonly Dictionary<ulong, List<ReportEntryData>> _reportListCache = new Dictionary<ulong, List<ReportEntryData>>();
        private readonly Dictionary<string, ulong> _reportListRequests = new Dictionary<string, ulong>();
        private readonly Dictionary<ulong, string> _openReportDetail = new Dictionary<ulong, string>();
        private readonly Dictionary<ulong, string> _pendingOpenReportId = new Dictionary<ulong, string>();

        /// Категория, выбранная на экране создания — прикрепится к следующему /report игрока.
        private readonly Dictionary<ulong, string> _pendingReportCategory = new Dictionary<ulong, string>();

        // ── /report ──────────────────────────────────────────────────

        private void CmdReport(IPlayer player, string command, string[] args)
        {
            if (!_config.Modules.Reports)
            {
                player.Reply("[Overpanel] Обращения отключены.");
                return;
            }

            var basePlayer = player.Object as BasePlayer;
            if (basePlayer == null) return;

            if (args.Length == 0)
            {
                RequestReportList(basePlayer);
                return;
            }

            if (args.Length < 2)
            {
                player.Reply("[Overpanel] Использование: /report <SteamID или имя> <причина>");
                player.Reply("[Overpanel] Ссылку на доказательства можно вставить прямо в текст.");
                player.Reply("[Overpanel] Или просто /report — список ваших обращений.");
                return;
            }

            var targetId = ResolveTarget(args[0]);
            var text     = string.Join(" ", args, 1, args.Length - 1);

            SubmitReport(basePlayer, targetId, text);
        }

        private string ResolveTarget(string query)
        {
            if (query.Length == 17 && query.All(char.IsDigit))
                return query;

            var found = BasePlayer.activePlayerList
                .FirstOrDefault(p => p.displayName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);

            return found?.UserIDString ?? query;
        }

        private void SubmitReport(BasePlayer author, string targetId, string text)
        {
            var attachments = ExtractAttachments(text);

            if (!_playerReportCounts.ContainsKey(author.UserIDString))
                _playerReportCounts[author.UserIDString] = 0;
            _playerReportCounts[author.UserIDString]++;

            // Категория выбрана на CUI-экране создания и ждала описания проблемы
            string subject = null;
            if (_pendingReportCategory.TryGetValue(author.userID, out var chosen))
            {
                subject = chosen;
                _pendingReportCategory.Remove(author.userID);
            }

            SendEvent("report.created", new Dictionary<string, object>
            {
                ["author_steamid"] = author.UserIDString,
                ["author_name"]    = author.displayName,
                ["target_steamid"] = targetId,
                ["text"]           = text,
                ["subject"]        = subject,
                ["attachments"]    = attachments,
            });

            SyncReportToIQReport(author.UserIDString, targetId, text);

            SendReply(author, "[Overpanel] Обращение отправлено. Ожидайте ответа администрации.");

            NotifyAdminsInGame(
                $"<color=#5599FF>[ОБРАЩЕНИЕ]</color> от <color=#ffcc00>{GetPlayerName(author)}</color>: {text}");
        }

        // ── CUI: список и переписка по обращениям ────────────────────

        private void RequestReportList(BasePlayer player)
        {
            if (!IsBackendConnected)
            {
                SendReply(player, "[Overpanel] Панель недоступна, попробуйте позже.");
                return;
            }

            var requestId = Guid.NewGuid().ToString("N");
            _reportListRequests[requestId] = player.userID;

            SendEvent("report.list_request", new Dictionary<string, object>
            {
                ["steamid"] = player.UserIDString,
            }, requestId);
        }

        private void HandleActionReportListResponse(JObject msg)
        {
            var requestId = msg["request_id"]?.ToString();
            if (string.IsNullOrEmpty(requestId) || !_reportListRequests.TryGetValue(requestId, out var userId))
                return;
            _reportListRequests.Remove(requestId);

            var player = BasePlayer.FindByID(userId);
            if (player == null || !player.IsConnected) return;

            var reports = msg["reports"]?.ToObject<List<ReportEntryData>>() ?? new List<ReportEntryData>();
            _reportListCache[userId] = reports;

            if (_pendingOpenReportId.TryGetValue(userId, out var pendingId))
            {
                _pendingOpenReportId.Remove(userId);
                if (reports.Any(r => r.Id == pendingId))
                {
                    ShowReportDetailScreen(player, pendingId);
                    return;
                }
            }

            ShowReportListScreen(player);
        }

        /// <summary>Игрок написал ответ администратору в открытом обращении.</summary>
        private void SendReportMessage(BasePlayer player, string reportId, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;

            SendEvent("report.message", new Dictionary<string, object>
            {
                ["report_id"]      = reportId,
                ["author_steamid"] = player.UserIDString,
                ["text"]           = text.Trim(),
            });

            // Оптимистично добавляем своё сообщение в кэш, не дожидаясь round-trip
            if (_reportListCache.TryGetValue(player.userID, out var reports))
            {
                var report = reports.FirstOrDefault(r => r.Id == reportId);
                report?.Messages.Add(new ReportMessageData
                {
                    AuthorType = "player",
                    Text = text.Trim(),
                    CreatedAt = DateTime.UtcNow.ToString("o"),
                });
            }

            ShowReportDetailScreen(player, reportId);
        }

        private void MarkReportResolved(BasePlayer player, string reportId)
        {
            SendEvent("report.closed", new Dictionary<string, object>
            {
                ["report_id"] = reportId,
                ["closed_by"] = player.UserIDString,
            });

            SendReply(player, $"[Overpanel] Обращение {reportId} закрыто.");
            RequestReportList(player);
        }

        private void MarkReportUrgent(BasePlayer player, string reportId)
        {
            SendEvent("report.mark_urgent", new Dictionary<string, object>
            {
                ["report_id"] = reportId,
                ["steamid"]   = player.UserIDString,
            });

            SendReply(player, $"[Overpanel] Обращение {reportId} помечено как актуальное.");

            if (_reportListCache.TryGetValue(player.userID, out var reports))
            {
                var report = reports.FirstOrDefault(r => r.Id == reportId);
                if (report != null) report.IsPriority = true;
            }

            ShowReportDetailScreen(player, reportId);
        }

        /// <summary>Достаёт ссылки на облачные хранилища из текста обращения.</summary>
        private List<string> ExtractAttachments(string text)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(text)) return result;

            foreach (var token in text.Split(' ', '\n', '\t'))
            {
                if (!token.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                    !token.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    continue;

                foreach (var host in AttachmentHosts)
                {
                    if (token.IndexOf(host, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        result.Add(token.Trim());
                        break;
                    }
                }
            }

            return result;
        }

        // ── /discord ─────────────────────────────────────────────────

        private void CmdDiscord(IPlayer player, string command, string[] args)
        {
            var basePlayer = player.Object as BasePlayer;
            if (basePlayer == null) return;

            if (_checkSessions.TryGetValue(player.Id, out var session) && args.Length > 0)
            {
                var tag = string.Join(" ", args).Trim();

                SendEvent("check.discord", new Dictionary<string, object>
                {
                    ["session_id"] = session.SessionId,
                    ["steamid"]    = player.Id,
                    ["discord"]    = tag,
                });

                player.Reply($"[Overpanel] Discord сохранён: {tag}");
                return;
            }

            if (!string.IsNullOrEmpty(_config.AppealServiceUrl))
                player.Reply($"[Overpanel] Discord/поддержка: {_config.AppealServiceUrl}");
            else
                player.Reply("[Overpanel] Discord не настроен администрацией.");
        }

        // ── /rules и /panel ──────────────────────────────────────────

        private void CmdRules(IPlayer player, string command, string[] args)
        {
            var basePlayer = player.Object as BasePlayer;
            if (basePlayer == null) return;

            ShowRulesScreen(basePlayer, GetServerRules());
        }

        private void CmdPanel(IPlayer player, string command, string[] args)
        {
            if (!string.IsNullOrEmpty(_config.PanelUrl))
                player.Reply($"[Overpanel] Панель: {_config.PanelUrl}");
        }

        // ── Правила сервера ──────────────────────────────────────────

        private string GetServerRules()
        {
            try
            {
                var rules = Interface.Oxide.DataFileSystem
                    .ReadObject<Dictionary<string, string>>("Overpanel/ServerRules");

                return rules != null && rules.TryGetValue("text", out var text) ? text : "";
            }
            catch
            {
                return "";
            }
        }

        internal void UpdateServerRules(string text)
        {
            Interface.Oxide.DataFileSystem.WriteObject("Overpanel/ServerRules",
                new Dictionary<string, string> { ["text"] = text });

            Puts("[Overpanel] Правила сервера обновлены.");
        }

        // ── Фидбек ───────────────────────────────────────────────────

        [ChatCommand("feedback")]
        private void CmdFeedback(BasePlayer player, string command, string[] args)
        {
            if (args.Length == 0)
            {
                SendReply(player, "[Overpanel] Использование: /feedback <текст>");
                return;
            }

            var text = string.Join(" ", args);

            SendEvent("feedback.created", new Dictionary<string, object>
            {
                ["author_steamid"] = player.UserIDString,
                ["text"]           = text,
            });

            SendReply(player, "[Overpanel] Спасибо! Ваш отзыв отправлен администрации.");
        }

        // ── WS-обработчики (панель → ответы на обращения) ────────────

        private void HandleActionReportMessage(JObject msg)
        {
            var targetId  = msg["target_steamid"]?.ToString();
            var reportId  = msg["report_id"]?.ToString();
            var text      = msg["text"]?.ToString();
            var adminName = msg["admin_title"]?.ToString() ?? "Администратор";

            if (string.IsNullOrEmpty(targetId) || string.IsNullOrEmpty(text)) return;

            var player = BasePlayer.Find(targetId);
            if (player == null || !player.IsConnected) return;

            SendReply(player,
                $"<color=#5599FF>[Обращение {reportId}]</color> <color=#66ff66>{adminName}</color>: {text}");

            var attachments = msg["attachments"]?.ToObject<List<string>>();
            if (attachments != null && attachments.Count > 0)
            {
                foreach (var url in attachments)
                    SendReply(player, $"<color=#888888>Вложение:</color> {url}");
            }
        }

        private void HandleActionReportClose(JObject msg)
        {
            var targetId = msg["target_steamid"]?.ToString();
            var reportId = msg["report_id"]?.ToString();

            if (string.IsNullOrEmpty(targetId)) return;

            var player = BasePlayer.Find(targetId);
            if (player != null && player.IsConnected)
                SendReply(player, $"<color=#66ff66>[Overpanel]</color> Ваше обращение {reportId} закрыто.");
        }

        /// <summary>Панель вернула ID созданного обращения — показываем его игроку в CUI.</summary>
        private void HandleActionReportRegistered(JObject msg)
        {
            var authorId = msg["author_steamid"]?.ToString();
            var reportId = msg["report_id"]?.ToString();

            if (string.IsNullOrEmpty(authorId)) return;

            var player = BasePlayer.Find(authorId);
            if (player == null || !player.IsConnected) return;

            SendReply(player, $"<color=#66ff66>[Overpanel]</color> Обращение принято. Номер: <color=#ffcc00>{reportId}</color>");

            // report.list_response ниже подхватит _pendingOpenReportId и откроет CUI обращения
            _pendingOpenReportId[player.userID] = reportId;
            RequestReportList(player);
        }

        #endregion

        #region RCON

        /// <summary>
        /// Исполнение RCON-команд, пришедших из панели.
        ///
        /// Чёрный список проверяется на стороне Backend (там известна роль
        /// администратора). Плагин выполняет уже разрешённую команду и
        /// возвращает вывод тем же request_id, что пришёл в rcon.exec —
        /// поэтому RCON не мешает потоку обычных событий в этом же сокете.
        /// </summary>

        private string _activeRconRequestId;
        private StringBuilder _rconCapture;
        private Timer _rconFlushTimer;

        private readonly object _rconLock = new object();

        private const int RCON_CAPTURE_WINDOW_MS = 400;
        private const int RCON_MAX_OUTPUT_CHARS  = 16000;

        // Постоянный стрим серверной консоли в панель
        private bool _consoleStreamEnabled = true;
        private readonly Queue<string> _consoleBuffer = new Queue<string>();
        private Timer _consoleFlushTimer;

        private void InitRconCapture()
        {
            Application.logMessageReceived += OnUnityLogMessage;

            // Общий лог консоли отдаём пачками, чтобы не забивать сокет
            _consoleFlushTimer = timer.Every(1f, FlushConsoleBuffer);
        }

        private void ShutdownRconCapture()
        {
            Application.logMessageReceived -= OnUnityLogMessage;
            _rconFlushTimer?.Destroy();
            _consoleFlushTimer?.Destroy();
        }

        // ── Исполнение команды из панели ─────────────────────────────

        private void HandleActionRconExec(JObject msg, string requestId)
        {
            // admin_steamid/admin_title раньше уходили только в LogRconExecution —
            // с тех пор как объединённую запись "команда + ответ" пишет бэкенд
            // при получении rcon.output, они плагину больше не нужны.
            var command = msg["command"]?.ToString();

            if (string.IsNullOrEmpty(command))
            {
                SendRconOutput(requestId, "Пустая команда", final: true, stream: "stderr");
                return;
            }

            lock (_rconLock)
            {
                _activeRconRequestId = requestId;
                _rconCapture = new StringBuilder();
            }

            string directResult = null;
            try
            {
                // ConsoleSystem.Run возвращает результат синхронно для большинства команд,
                // остальное осядет в перехватчике логов
                directResult = ConsoleSystem.Run(ConsoleSystem.Option.Server.Quiet(), command);
            }
            catch (Exception ex)
            {
                lock (_rconLock)
                {
                    _activeRconRequestId = null;
                    _rconCapture = null;
                }

                // Лог со связкой "команда + ответ" пишет бэкенд при получении rcon.output —
                // здесь достаточно отправить сам вывод.
                SendRconOutput(requestId, $"Ошибка выполнения: {ex.Message}", final: true, stream: "stderr");
                return;
            }

            if (!string.IsNullOrEmpty(directResult))
            {
                lock (_rconLock)
                {
                    _rconCapture?.AppendLine(directResult);
                }
            }

            // Даём команде время дописать асинхронный вывод в консоль
            _rconFlushTimer?.Destroy();
            _rconFlushTimer = timer.Once(RCON_CAPTURE_WINDOW_MS / 1000f, () => FinishRconCapture(requestId));
        }

        private void FinishRconCapture(string requestId)
        {
            string output;

            lock (_rconLock)
            {
                if (_activeRconRequestId != requestId) return;

                output = _rconCapture?.ToString() ?? "";
                _activeRconRequestId = null;
                _rconCapture = null;
            }

            if (output.Length > RCON_MAX_OUTPUT_CHARS)
                output = output.Substring(0, RCON_MAX_OUTPUT_CHARS) + "\n... вывод обрезан ...";

            if (string.IsNullOrWhiteSpace(output))
                output = "Команда выполнена (нет вывода).";

            SendRconOutput(requestId, output, final: true);
        }

        private void SendRconOutput(string requestId, string output, bool final, string stream = "stdout")
        {
            SendEvent("rcon.output", new Dictionary<string, object>
            {
                ["request_id"] = requestId,
                ["output"]     = output,
                ["stream"]     = stream,
                ["final"]      = final,
            }, requestId);
        }

        // ── Перехват вывода консоли ──────────────────────────────────

        private void OnUnityLogMessage(string condition, string stackTrace, LogType type)
        {
            if (string.IsNullOrEmpty(condition)) return;

            // Строки самого плагина не отправляем обратно — иначе получится эхо
            if (condition.StartsWith("[Overpanel]", StringComparison.Ordinal)) return;

            lock (_rconLock)
            {
                if (_rconCapture != null && _rconCapture.Length < RCON_MAX_OUTPUT_CHARS)
                    _rconCapture.AppendLine(condition);
            }

            if (!_consoleStreamEnabled) return;

            lock (_consoleBuffer)
            {
                if (_consoleBuffer.Count >= 200) _consoleBuffer.Dequeue();
                _consoleBuffer.Enqueue($"{MapLogLevel(type)}|{condition}");
            }
        }

        private void FlushConsoleBuffer()
        {
            if (!IsBackendConnected) return;

            List<string> batch = null;

            lock (_consoleBuffer)
            {
                if (_consoleBuffer.Count == 0) return;

                batch = new List<string>(_consoleBuffer.Count);
                while (_consoleBuffer.Count > 0)
                    batch.Add(_consoleBuffer.Dequeue());
            }

            foreach (var entry in batch)
            {
                var sep = entry.IndexOf('|');
                if (sep < 0) continue;

                SendEvent("console.log", new Dictionary<string, object>
                {
                    ["level"]   = entry.Substring(0, sep),
                    ["message"] = entry.Substring(sep + 1),
                });
            }
        }

        private static string MapLogLevel(LogType type)
        {
            switch (type)
            {
                case LogType.Error:
                case LogType.Exception: return "error";
                case LogType.Warning:   return "warn";
                default:                return "info";
            }
        }

        #endregion

        #region Player Hooks & Chat

        // ── Подключение игроков ──────────────────────────────────────

        void OnPlayerConnected(BasePlayer player)
        {
            var ip = GetPlayerIp(player);
            var isPirate = IsPlayerPirate(player);

            SendEvent("player.connected", new Dictionary<string, object>
            {
                ["steamid"] = player.UserIDString,
                ["name"]    = player.displayName,
                ["ip"]      = ip,
                ["team_id"] = player.currentTeam,
            });

            if (isPirate)
            {
                SendEvent("player.piratedetect", new Dictionary<string, object>
                {
                    ["steamid"] = player.UserIDString,
                    ["name"]    = player.displayName,
                });

                Puts($"[Overpanel] Обнаружен пиратский клиент: {player.displayName} ({player.UserIDString})");
            }
        }

        void OnPlayerSleepEnded(BasePlayer player)
        {
            // Восстанавливаем оверлей, если проверка началась пока игрок спал
            if (_checkSessions.ContainsKey(player.UserIDString))
                ShowCheckOverlay(player);
        }

        private bool IsPlayerPirate(BasePlayer player)
        {
            var id = player.UserIDString;
            if (id.Length != 17) return true;
            if (!id.StartsWith("765611", StringComparison.Ordinal)) return true;
            return false;
        }

        // ── Игроки: вспомогательное ──────────────────────────────────

        private string GetPlayerName(BasePlayer player)
        {
            return $"<color=#CCCCCC>{player.displayName}</color> ({player.UserIDString})";
        }

        private string GetPlayerLanguage(BasePlayer player)
        {
            return lang.GetLanguage(player.UserIDString) ?? "ru";
        }

        private AdminData GetOrCreateAdminData(string steamId, string name)
        {
            if (!_adminsCache.TryGetValue(steamId, out var data))
            {
                data = new AdminData { SteamId = steamId, Name = name };
                _adminsCache[steamId] = data;
            }
            return data;
        }

        private string GetAdminTitle(string steamId, string fallback)
        {
            return _adminsCache.TryGetValue(steamId, out var admin) ? admin.Title : fallback;
        }

        // ── Админ-чат ────────────────────────────────────────────────

        [ChatCommand("a")]
        private void CmdAdminChat(BasePlayer player, string command, string[] args)
        {
            if (!HasPermission(player.UserIDString, "overpanel.adminchat.view"))
                return;

            var msg = args.Length > 0 ? string.Join(" ", args) : "";
            if (string.IsNullOrEmpty(msg))
            {
                SendReply(player, "[Overpanel] Использование: /a <сообщение>");
                return;
            }

            var title = GetAdminTitle(player.UserIDString, player.displayName);
            var adminMsg = $"<color=#5599FF>[ADMIN]</color> <color=#66ff66>{title}</color>: {msg}";

            foreach (var steamId in _adminsCache.Keys)
            {
                var adminPlayer = BasePlayer.Find(steamId);
                if (adminPlayer?.IsConnected == true)
                    SendReply(adminPlayer, adminMsg);
            }

            SendEvent("player.chat", new Dictionary<string, object>
            {
                ["steamid"] = player.UserIDString,
                ["name"]    = title,
                ["message"] = msg,
                ["channel"] = "Admin",
            });
        }

        private void ChatAlert(string message)
        {
            Server.Broadcast(message);
        }

        private void NotifyAdminsInGame(string message)
        {
            foreach (var steamId in _adminsCache.Keys)
            {
                var adminPlayer = BasePlayer.Find(steamId);
                if (adminPlayer?.IsConnected == true)
                    SendReply(adminPlayer, message);
            }
        }

        // ── Карта ────────────────────────────────────────────────────

        private Timer _mapTimer;

        private void InitMapTimer()
        {
            if (!_config.Modules.Map) return;
            _mapTimer = timer.Every(_config.MapUpdateInterval, SendMapSnapshot);
        }

        private void SendMapSnapshot()
        {
            if (!IsBackendConnected) return;

            var snapshot = new List<Dictionary<string, object>>();

            foreach (var player in BasePlayer.activePlayerList)
            {
                var pos = player.transform.position;
                snapshot.Add(new Dictionary<string, object>
                {
                    ["steamid"] = player.UserIDString,
                    ["name"]    = player.displayName,
                    ["x"]       = pos.x,
                    ["y"]       = pos.y,
                    ["z"]       = pos.z,
                    ["health"]  = (int)player.health,
                    ["isAdmin"] = _adminsCache.ContainsKey(player.UserIDString),
                });
            }

            SendEvent("player.position_batch", new Dictionary<string, object>
            {
                ["players"]  = snapshot,
                ["online"]   = BasePlayer.activePlayerList.Count,
                ["sleeping"] = BasePlayer.sleepingPlayerList.Count,
            });
        }

        // ── Форматирование ───────────────────────────────────────────

        private string FormatDuration(int seconds)
        {
            if (seconds <= 0) return "навсегда";

            var t = TimeSpan.FromSeconds(seconds);
            if (t.TotalDays >= 1)  return $"{(int)t.TotalDays}д {t.Hours}ч";
            if (t.TotalHours >= 1) return $"{(int)t.TotalHours}ч {t.Minutes}м";
            return $"{t.Minutes}м {t.Seconds}с";
        }

        // ── WS-обработчики (панель → чат/телепорт) ───────────────────

        private void HandleActionChatSend(JObject msg)
        {
            var targetId = msg["target_steamid"]?.ToString();
            var message  = msg["message"]?.ToString();
            var senderTitle = msg["admin_title"]?.ToString();
            var notificationType = msg["notification_type"]?.ToString();
            var reportId = msg["report_id"]?.ToString();

            if (string.IsNullOrEmpty(message)) return;

            if (string.IsNullOrEmpty(targetId))
            {
                var broadcastText = string.IsNullOrEmpty(senderTitle)
                    ? $"[Overpanel] {message}"
                    : $"<color=#5599FF>[{senderTitle}]</color> {message}";
                Server.Broadcast(broadcastText);
                return;
            }

            var player = BasePlayer.Find(targetId);
            if (player == null || !player.IsConnected) return;

            if (notificationType == "report.admin_message" && !string.IsNullOrEmpty(reportId))
            {
                var adminTitle = string.IsNullOrEmpty(senderTitle) ? "Администратор" : senderTitle;
                SendReply(player, $"<color=#5599FF>[Обращение {reportId}]</color> <color=#66ff66>{adminTitle}</color>: {message}");
                HandleIncomingReportReply(player, reportId, message);
                return;
            }

            var text = string.IsNullOrEmpty(senderTitle)
                ? $"[Overpanel] {message}"
                : $"<color=#5599FF>[{senderTitle}]</color> {message}";
            SendReply(player, text);
        }

        /// <summary>Обновляет локальный кэш /report CUI и перерисовывает открытый экран, если он открыт.</summary>
        private void HandleIncomingReportReply(BasePlayer player, string reportId, string text)
        {
            if (_reportListCache.TryGetValue(player.userID, out var reports))
            {
                var report = reports.FirstOrDefault(r => r.Id == reportId);
                if (report != null)
                {
                    report.Messages.Add(new ReportMessageData
                    {
                        AuthorType = "admin",
                        Text = text,
                        CreatedAt = DateTime.UtcNow.ToString("o"),
                    });
                    if (report.Status == "new") report.Status = "in_progress";
                }
            }

            if (_openReportDetail.TryGetValue(player.userID, out var openId) && openId == reportId)
                ShowReportDetailScreen(player, reportId);
        }

        private void HandleActionTeleport(JObject msg)
        {
            var targetId = msg["target_steamid"]?.ToString();
            if (string.IsNullOrEmpty(targetId)) return;

            var player = BasePlayer.Find(targetId);
            if (player == null || !player.IsConnected) return;

            var x = msg["x"]?.ToObject<float>() ?? 0f;
            var y = msg["y"]?.ToObject<float>() ?? 0f;
            var z = msg["z"]?.ToObject<float>() ?? 0f;

            player.Teleport(new UnityEngine.Vector3(x, y, z));
        }

        #endregion

        #region Integrations

        [PluginReference] Plugin IQChat;
        [PluginReference] Plugin IQReportSystem;
        [PluginReference] Plugin IQBanSystem;
        [PluginReference] Plugin ImageLibrary;

        private bool _iqChatLoaded;
        private bool _iqReportLoaded;
        private bool _iqBanLoaded;
        private bool _imageLibraryLoaded;

        private void DetectIntegrations()
        {
            _iqChatLoaded       = IQChat != null;
            _iqReportLoaded     = IQReportSystem != null;
            _iqBanLoaded        = IQBanSystem != null;
            _imageLibraryLoaded = ImageLibrary != null;

            if (_iqChatLoaded)    Puts("[Overpanel] Integration: IQChat ✓");
            if (_iqReportLoaded)  Puts("[Overpanel] Integration: IQReportSystem ✓");
            if (_iqBanLoaded)     Puts("[Overpanel] Integration: IQBanSystem ✓");
            if (_imageLibraryLoaded) Puts("[Overpanel] Integration: ImageLibrary ✓");
        }

        void OnPluginLoaded(Plugin plugin)
        {
            var changed = true;

            switch (plugin.Name)
            {
                case "IQChat":         IQChat = plugin;         _iqChatLoaded = true;       break;
                case "IQReportSystem": IQReportSystem = plugin; _iqReportLoaded = true;     break;
                case "IQBanSystem":    IQBanSystem = plugin;    _iqBanLoaded = true;        break;
                case "ImageLibrary":   ImageLibrary = plugin;   _imageLibraryLoaded = true; break;
                default: changed = false; break;
            }

            if (changed)
            {
                SendEvent("integration.detected", new Dictionary<string, object>
                {
                    ["plugin_name"] = plugin.Name,
                });
            }
        }

        void OnPluginUnloaded(Plugin plugin)
        {
            var changed = true;

            switch (plugin.Name)
            {
                case "IQChat":         _iqChatLoaded = false;       break;
                case "IQReportSystem": _iqReportLoaded = false;     break;
                case "IQBanSystem":    _iqBanLoaded = false;        break;
                case "ImageLibrary":   _imageLibraryLoaded = false; break;
                default: changed = false; break;
            }

            if (changed)
            {
                SendEvent("integration.unloaded", new Dictionary<string, object>
                {
                    ["plugin_name"] = plugin.Name,
                });
            }
        }

        internal bool HasIntegration(string name)
        {
            switch (name)
            {
                case "IQChat":         return _iqChatLoaded;
                case "IQReportSystem": return _iqReportLoaded;
                case "IQBanSystem":    return _iqBanLoaded;
                case "ImageLibrary":   return _imageLibraryLoaded;
                default:               return false;
            }
        }

        internal List<string> GetDetectedIntegrations()
        {
            var result = new List<string>();
            if (_iqChatLoaded)       result.Add("IQChat");
            if (_iqReportLoaded)     result.Add("IQReportSystem");
            if (_iqBanLoaded)        result.Add("IQBanSystem");
            if (_imageLibraryLoaded) result.Add("ImageLibrary");
            return result;
        }

        // ======= IQChat =======

        private void SyncMuteToIQChat(BasePlayer player, MuteData mute)
        {
            if (!_iqChatLoaded || IQChat == null) return;
            try
            {
                int durationSeconds = mute.Expires == DateTime.MaxValue ? 0 : (int)(mute.Expires - DateTime.UtcNow).TotalSeconds;
                IQChat.Call("API_MUTE_PLAYER", player.UserIDString, mute.Reason, durationSeconds);
            }
            catch (Exception ex)
            {
                PrintWarning($"[Overpanel] IQChat mute sync failed: {ex.Message}");
            }
        }

        private void SyncBanToIQBanSystem(BasePlayer player, string reason, int durationSeconds)
        {
            if (!_iqBanLoaded || IQBanSystem == null) return;
            try
            {
                IQBanSystem.Call("API_BAN_PLAYER", player.UserIDString, reason, durationSeconds);
            }
            catch (Exception ex)
            {
                PrintWarning($"[Overpanel] IQBanSystem ban sync failed: {ex.Message}");
            }
        }

        private void SyncReportToIQReport(string authorId, string targetId, string reason)
        {
            if (!_iqReportLoaded || IQReportSystem == null) return;
            try
            {
                IQReportSystem.Call("API_CREATE_REPORT", authorId, targetId, reason);
            }
            catch (Exception ex)
            {
                PrintWarning($"[Overpanel] IQReportSystem sync failed: {ex.Message}");
            }
        }

        // ======= Локальный фон /report =======

        private uint? _reportBgCrc;

        /// <summary>
        /// Грузит data/Overpanel/images/REPORT_SCREEN.png (1202×805) через FileStorage,
        /// без внешнего хостинга и без ImageLibrary — просто кладёте файл на сервер.
        /// Если файла нет, экраны /report используют сплошную заливку.
        /// </summary>
        private void LoadLocalReportBackground()
        {
            var path = Path.Combine(Interface.Oxide.DataDirectory, "Overpanel", "images", "REPORT_SCREEN.png");
            if (!File.Exists(path))
            {
                _reportBgCrc = null;
                return;
            }

            try
            {
                var bytes = File.ReadAllBytes(path);
                _reportBgCrc = FileStorage.server.Store(bytes, FileStorage.Type.png, CommunityEntity.ServerInstance.net.ID);
                Puts("[Overpanel] Фон /report загружен из data/Overpanel/images/REPORT_SCREEN.png");
            }
            catch (Exception ex)
            {
                PrintWarning($"[Overpanel] Не удалось загрузить REPORT_SCREEN.png: {ex.Message}");
                _reportBgCrc = null;
            }
        }

        #endregion
    }
}
