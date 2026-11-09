// In-game custom song browser: a "SONGS +" button in the menus opens a full-screen panel that
// browses BeatSaver (search, top rated, latest, curated, ranked), previews and downloads maps
// into the custom song folder, and lists/deletes installed songs. Drawn with IMGUI on top of
// the 3D menus; while it is open TouchInput ignores the screen.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace BeatSaberPort
{
    public class SongBrowser : MonoBehaviour
    {
        public static SongBrowser Instance { get; private set; }
        public static bool IsOpen { get { return Instance != null && Instance._open; } }

        // true when a touch starting at this screen point (Input coordinates) belongs to the browser
        public static bool Blocks(Vector2 p)
        {
            var me = Instance;
            if (me == null) return false;
            if (me._open) return true;
            return me._pillShown && me._pill.Contains(me.ToLogical(p));
        }

        public static void Create(GameObject root)
        {
            if (Instance == null) Instance = root.AddComponent<SongBrowser>();
        }

        // ------------------------------------------------------------------ state
        class Download
        {
            public BsMap map;
            public string state = "queued";   // queued, downloading, installing, done, failed
            public float progress;
            public string message;
        }

        const float LogicalHeight = 560f;
        const float RowH = 78f, TopH = 112f, StatusH = 30f;
        const int MaxDownloads = 2, MaxCoverLoads = 4;

        bool _open, _pillShown;
        Rect _pill;
        float _s = 1f;           // physical pixels per logical unit
        Rect _safe;              // logical safe area
        BsTab _tab = BsTab.Top;
        string _query = "";
        bool _hideModMaps = true, _typing;
        TouchScreenKeyboard _kb;

        readonly List<BsMap> _maps = new List<BsMap>();
        int _page, _generation;
        bool _loading, _end;
        string _status = "";

        List<Songs.Local> _local = new List<Songs.Local>();
        string _confirmDelete;
        float _confirmUntil;

        readonly Dictionary<string, Texture2D> _covers = new Dictionary<string, Texture2D>();
        readonly HashSet<string> _coverLoading = new HashSet<string>();
        readonly Dictionary<string, Download> _downloads = new Dictionary<string, Download>();
        readonly List<Download> _queue = new List<Download>();
        int _activeDownloads;
        HashSet<string> _installed = new HashSet<string>();

        AudioSource _audio;
        string _previewKey;
        readonly List<AudioSource> _pausedMusic = new List<AudioSource>();

        // pointer / scrolling
        struct Hit { public Rect r; public Action a; }
        readonly List<Hit> _hits = new List<Hit>();
        readonly List<Hit> _hitsNext = new List<Hit>();
        bool _pDown, _pDrag;
        Vector2 _pStart, _pLast;
        float _scroll, _vel, _maxScroll;
        Rect _listRect;

        // drawing
        GUIStyle _title, _sub, _small, _btn, _tabStyle, _big;
        float _styleScale;
        Vector2 _origin;   // logical offset of the current GUI group
        Rect _clip;        // logical clip for hit registration

        static readonly Color Bg = new Color(0.05f, 0.05f, 0.08f, 0.97f);
        static readonly Color Panel = new Color(0.13f, 0.13f, 0.18f, 1f);
        static readonly Color Accent = new Color(0.17f, 0.45f, 0.95f, 1f);
        static readonly Color Good = new Color(0.2f, 0.65f, 0.35f, 1f);
        static readonly Color Bad = new Color(0.8f, 0.25f, 0.25f, 1f);
        static readonly Color Warn = new Color(1f, 0.65f, 0.2f, 1f);
        static readonly Color Dim = new Color(0.7f, 0.72f, 0.8f, 1f);

        void Awake()
        {
            useGUILayout = false;
            _audio = gameObject.AddComponent<AudioSource>();
            _audio.playOnAwake = false;
            _audio.loop = true;
            _audio.volume = 0.8f;
        }

        Vector2 ToLogical(Vector2 inputPos)
        {
            return new Vector2(inputPos.x / _s, (Screen.height - inputPos.y) / _s);
        }

        // ------------------------------------------------------------------ open / close
        void Open()
        {
            _open = true;
            _installed = Songs.InstalledKeys();
            if (_tab == BsTab.Mine) LoadLocal();
            else if (_maps.Count == 0 && !_loading) Reload();
            Debug.Log("[BeatSaberPort] song browser opened");
        }

        void Close()
        {
            _open = false;
            _typing = false;
            if (_kb != null) { _kb.active = false; _kb = null; }
            StopPreview();
            if (Songs.pendingRefresh) Songs.RefreshGameList();
        }

        void SetTab(BsTab t)
        {
            if (t == BsTab.Search && _tab == BsTab.Search) { EditQuery(); return; }
            _tab = t;
            _scroll = 0f; _vel = 0f;
            if (t == BsTab.Mine) { LoadLocal(); return; }
            if (t == BsTab.Search && string.IsNullOrEmpty(_query)) { _maps.Clear(); _end = true; EditQuery(); return; }
            Reload();
        }

        void EditQuery()
        {
            if (TouchScreenKeyboard.isSupported)
                _kb = TouchScreenKeyboard.Open(_query, TouchScreenKeyboardType.Default, false, false, false, false, "Song, artist, mapper or !bsr key");
            else
                _typing = true;   // no on-screen keyboard: type on a hardware keyboard
        }

        // ------------------------------------------------------------------ BeatSaver lists
        void Reload()
        {
            _generation++;
            _maps.Clear();
            _page = 0; _end = false; _loading = false;
            _scroll = 0f; _vel = 0f;
            foreach (var t in _covers.Values) if (t != null) Destroy(t);
            _covers.Clear(); _coverLoading.Clear();
            StartCoroutine(LoadPage(_generation));
        }

        IEnumerator LoadPage(int gen)
        {
            _loading = true;
            _status = "Loading…";
            string key = _tab == BsTab.Search && _page == 0 ? BeatSaver.KeyFromQuery(_query) : null;
            if (key != null)
            {
                using (var r = UnityWebRequest.Get(BeatSaver.MapUrl(key)))
                {
                    r.SetRequestHeader("User-Agent", BeatSaver.UserAgent);
                    yield return r.SendWebRequest();
                    if (gen != _generation) yield break;
                    if (!r.isNetworkError && !r.isHttpError)
                    {
                        BsMap m = null;
                        try { m = BsJson.Map(Newtonsoft.Json.Linq.JObject.Parse(r.downloadHandler.text)); } catch (Exception e) { Debug.Log("[BeatSaberPort] bsr parse: " + e.Message); }
                        if (m != null && m.Latest != null)
                        {
                            _maps.Add(m); _end = true; _loading = false;
                            _status = "Map " + key;
                            yield break;
                        }
                    }
                }
            }
            string url = BeatSaver.ListUrl(_tab, _query, _page, _hideModMaps);
            using (var r = UnityWebRequest.Get(url))
            {
                r.SetRequestHeader("User-Agent", BeatSaver.UserAgent);
                yield return r.SendWebRequest();
                if (gen != _generation) yield break;
                _loading = false;
                if (r.isNetworkError || r.isHttpError)
                {
                    _status = "Couldn't reach BeatSaver (" + (r.isNetworkError ? r.error : "HTTP " + r.responseCode) + "). Tap a tab to retry.";
                    _end = true;
                    yield break;
                }
                List<BsMap> docs;
                string body = r.downloadHandler.text;
                try { docs = BsJson.Page(body); }
                catch (Exception e) { _status = "Bad reply from BeatSaver: " + e.Message; _end = true; yield break; }
                int added = 0;
                foreach (var m in docs)
                    if (m.Latest != null && !(_hideModMaps && m.AllNeedMods)) { _maps.Add(m); added++; }
                if (docs.Count == 0) _end = true;
                Debug.Log("[BeatSaberPort] beatsaver " + url + " -> " + r.responseCode + ", " + (body != null ? body.Length : 0) + " chars, " +
                          docs.Count + " maps, " + added + " usable");
                _page++;
                _status = _maps.Count == 0 ? "No maps found." : _maps.Count + " maps";
            }
        }

        // ------------------------------------------------------------------ downloads
        void Get(BsMap m)
        {
            Download d;
            if (_downloads.TryGetValue(m.id, out d) && d.state != "failed") return;
            d = new Download { map = m };
            _downloads[m.id] = d;
            _queue.Add(d);
        }

        void PumpDownloads()
        {
            while (_activeDownloads < MaxDownloads && _queue.Count > 0)
            {
                var d = _queue[0];
                _queue.RemoveAt(0);
                _activeDownloads++;
                StartCoroutine(RunDownload(d));
            }
        }

        IEnumerator RunDownload(Download d)
        {
            var v = d.map.Latest;
            Directory.CreateDirectory(Songs.IncomingDir);
            string zip = Path.Combine(Songs.IncomingDir, v.hash + ".zip");
            d.state = "downloading";
            using (var r = new UnityWebRequest(v.downloadURL, "GET", new DownloadHandlerFile(zip) { removeFileOnAbort = true }, null))
            {
                r.SetRequestHeader("User-Agent", BeatSaver.UserAgent);
                var op = r.SendWebRequest();
                while (!op.isDone) { d.progress = r.downloadProgress; yield return null; }
                if (r.isNetworkError || r.isHttpError)
                {
                    d.state = "failed";
                    d.message = "download failed (" + (r.isNetworkError ? r.error : "HTTP " + r.responseCode) + ")";
                    _activeDownloads--;
                    yield break;
                }
            }
            d.state = "installing";
            string folder = Songs.FolderName(d.map.id, d.map.metadata.songName, d.map.metadata.levelAuthorName);
            var task = Task.Run(() => Songs.InstallZip(zip, folder));
            while (!task.IsCompleted) yield return null;
            try { File.Delete(zip); } catch { }
            var res = task.IsFaulted ? new Songs.Result { message = task.Exception.GetBaseException().Message } : task.Result;
            if (res.ok)
            {
                d.state = "done";
                _installed.Add(d.map.id.ToLowerInvariant());
                Songs.pendingRefresh = true;
                _status = "Installed " + d.map.metadata.songName;
            }
            else
            {
                d.state = "failed";
                d.message = res.message;
                _status = d.map.metadata.songName + ": " + res.message;
            }
            Debug.Log("[BeatSaberPort] download " + d.map.id + ": " + d.state + " " + d.message);
            _activeDownloads--;
        }

        // ------------------------------------------------------------------ covers + preview
        void WantCover(string id, string urlOrPath, bool local)
        {
            if (string.IsNullOrEmpty(urlOrPath) || _covers.ContainsKey(id) || _coverLoading.Contains(id)) return;
            if (_coverLoading.Count >= MaxCoverLoads) return;
            _coverLoading.Add(id);
            if (local)
            {
                Texture2D t = null;
                try
                {
                    if (File.Exists(urlOrPath))
                    {
                        t = new Texture2D(2, 2);
                        if (!t.LoadImage(File.ReadAllBytes(urlOrPath), true)) { Destroy(t); t = null; }
                    }
                }
                catch { t = null; }
                _covers[id] = t;
                _coverLoading.Remove(id);
                return;
            }
            StartCoroutine(LoadCover(id, urlOrPath, _generation));
        }

        IEnumerator LoadCover(string id, string url, int gen)
        {
            using (var r = UnityWebRequestTexture.GetTexture(url, true))
            {
                yield return r.SendWebRequest();
                if (gen != _generation) yield break;
                _coverLoading.Remove(id);
                _covers[id] = r.isNetworkError || r.isHttpError ? null : DownloadHandlerTexture.GetContent(r);
            }
        }

        void TogglePreview(BsMap m)
        {
            if (_previewKey == m.id) { StopPreview(); return; }
            StopPreview();
            _previewKey = m.id;
            StartCoroutine(PlayPreview(m));
        }

        IEnumerator PlayPreview(BsMap m)
        {
            using (var r = UnityWebRequestMultimedia.GetAudioClip(m.Latest.previewURL, AudioType.MPEG))
            {
                yield return r.SendWebRequest();
                if (_previewKey != m.id) yield break;
                AudioClip clip = null;
                if (!r.isNetworkError && !r.isHttpError)
                    try { clip = DownloadHandlerAudioClip.GetContent(r); } catch (Exception e) { Debug.Log("[BeatSaberPort] preview: " + e.Message); }
                if (clip == null) { _status = "Preview unavailable"; _previewKey = null; yield break; }
                // hush the menu music while previewing
                var spp = FindObjectOfType<SongPreviewPlayer>();
                if (spp != null)
                    foreach (var s in spp.GetComponentsInChildren<AudioSource>())
                        if (s.isPlaying) { s.Pause(); _pausedMusic.Add(s); }
                _audio.clip = clip;
                _audio.Play();
            }
        }

        void StopPreview()
        {
            _previewKey = null;
            if (_audio.isPlaying) _audio.Stop();
            if (_audio.clip != null) { Destroy(_audio.clip); _audio.clip = null; }
            foreach (var s in _pausedMusic) if (s != null) s.UnPause();
            _pausedMusic.Clear();
        }

        // ------------------------------------------------------------------ installed songs
        void LoadLocal()
        {
            _local = Songs.ListInstalled();
            foreach (var t in _covers.Values) if (t != null) Destroy(t);
            _covers.Clear(); _coverLoading.Clear();
            _scroll = 0f;
            _status = _local.Count + " installed songs — folder: Files app › On My iPhone › Beat Saber › CustomLevels";
        }

        void DeleteLocal(Songs.Local l)
        {
            if (_confirmDelete != l.path || Time.unscaledTime > _confirmUntil)
            {
                _confirmDelete = l.path; _confirmUntil = Time.unscaledTime + 3f;
                return;
            }
            _confirmDelete = null;
            try { Songs.Delete(l.path); _status = "Deleted " + l.title; }
            catch (Exception e) { _status = "Delete failed: " + e.Message; }
            _installed = Songs.InstalledKeys();
            float keep = _scroll;
            LoadLocal();
            _scroll = keep;
        }

        // ------------------------------------------------------------------ update / input
        void Update()
        {
            _s = Mathf.Max(0.5f, Screen.height / LogicalHeight);
            var sa = Screen.safeArea;
            _safe = new Rect(sa.x / _s, (Screen.height - sa.yMax) / _s, sa.width / _s, sa.height / _s);
            _pillShown = !_open && !TouchInput.InGameplay;
            if (!_open && Songs.pendingRefresh && !_pDown) Songs.RefreshGameList();
            PumpDownloads();
            if (_kb != null)
            {
                var st = _kb.status;
                if (st == TouchScreenKeyboard.Status.Done) { _query = _kb.text; _kb = null; _tab = BsTab.Search; Reload(); }
                else if (st == TouchScreenKeyboard.Status.Canceled || st == TouchScreenKeyboard.Status.LostFocus) _kb = null;
                else _query = _kb.text;
            }
            if (!_open && !_pillShown) { _pDown = false; return; }
            Pointer();
            if (_open && _tab != BsTab.Mine && !_loading && !_end && _scroll > _maxScroll - RowH * 4) StartCoroutine(LoadPage(_generation));
        }

        void Pointer()
        {
            bool has = false, began = false, ended = false;
            Vector2 p = Vector2.zero;
            if (Input.touchCount > 0)
            {
                var t = Input.GetTouch(0);
                has = true; p = t.position;
                began = t.phase == TouchPhase.Began;
                ended = t.phase == TouchPhase.Ended || t.phase == TouchPhase.Canceled;
            }
            else if (Input.GetMouseButton(0) || Input.GetMouseButtonUp(0))
            {
                has = true; p = Input.mousePosition;
                began = Input.GetMouseButtonDown(0); ended = Input.GetMouseButtonUp(0);
            }
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.05f);
            var g = ToLogical(p);
            if (has && began)
            {
                if (!_open && !_pill.Contains(g)) return;   // the game's touch, not ours
                _pDown = true; _pDrag = false; _pStart = _pLast = g; _vel = 0f;
            }
            else if (has && _pDown)
            {
                float dy = g.y - _pLast.y;
                if (!_pDrag && Mathf.Abs(g.y - _pStart.y) > 12f) _pDrag = true;
                if (_pDrag && _open && _listRect.Contains(_pStart))
                {
                    _scroll -= dy;
                    _vel = dt > 0f ? -dy / dt : 0f;
                }
                _pLast = g;
            }
            if (ended && _pDown)
            {
                _pDown = false;
                if (!_pDrag) Tap(_pStart);
            }
            if (!_pDown && _vel != 0f)
            {
                _scroll += _vel * dt;
                _vel *= Mathf.Exp(-5f * dt);
                if (Mathf.Abs(_vel) < 20f) _vel = 0f;
            }
            _scroll = Mathf.Clamp(_scroll, 0f, Mathf.Max(0f, _maxScroll));
        }

        void Tap(Vector2 g)
        {
            if (!_open)
            {
                if (_pill.Contains(g)) Open();
                return;
            }
            for (int i = _hits.Count - 1; i >= 0; i--)
                if (_hits[i].r.Contains(g)) { _hits[i].a(); return; }
        }

        // ------------------------------------------------------------------ drawing
        Rect R(float x, float y, float w, float h) { return new Rect(x * _s, y * _s, w * _s, h * _s); }

        // IMGUI colours get the sRGB conversion of the game's linear colour space; pre-linearise
        static Color Lin(Color c)
        {
            return QualitySettings.activeColorSpace == ColorSpace.Linear ? new Color(c.linear.r, c.linear.g, c.linear.b, c.a) : c;
        }

        void Fill(float x, float y, float w, float h, Color c)
        {
            var old = GUI.color; GUI.color = Lin(c);
            GUI.DrawTexture(R(x, y, w, h), Texture2D.whiteTexture);
            GUI.color = old;
        }

        void Text(float x, float y, float w, float h, string s, GUIStyle st, Color c)
        {
            var old = st.normal.textColor; st.normal.textColor = Lin(c);
            GUI.Label(R(x, y, w, h), s, st);
            st.normal.textColor = old;
        }

        void Button(float x, float y, float w, float h, string label, Color c, Action a)
        {
            Fill(x, y, w, h, c);
            Text(x, y, w, h, label, _btn, Color.white);
            if (a == null) return;
            var r = new Rect(_origin.x + x, _origin.y + y, w, h);
            if (_clip.width > 0f) { if (!r.Overlaps(_clip)) return; r = Intersect(r, _clip); }
            _hitsNext.Add(new Hit { r = r, a = a });
        }

        static Rect Intersect(Rect a, Rect b)
        {
            float x0 = Mathf.Max(a.xMin, b.xMin), y0 = Mathf.Max(a.yMin, b.yMin);
            float x1 = Mathf.Min(a.xMax, b.xMax), y1 = Mathf.Min(a.yMax, b.yMax);
            return new Rect(x0, y0, Mathf.Max(0, x1 - x0), Mathf.Max(0, y1 - y0));
        }

        void Styles()
        {
            if (_title != null && Mathf.Approximately(_styleScale, _s)) return;
            _styleScale = _s;
            Func<int, bool, TextAnchor, GUIStyle> mk = (size, bold, anchor) =>
            {
                var st = new GUIStyle(GUI.skin.label)
                {
                    fontSize = Mathf.RoundToInt(size * _s),
                    fontStyle = bold ? FontStyle.Bold : FontStyle.Normal,
                    alignment = anchor,
                    clipping = TextClipping.Clip,
                    wordWrap = false,
                    padding = new RectOffset(0, 0, 0, 0),
                    margin = new RectOffset(0, 0, 0, 0)
                };
                st.normal.textColor = Color.white;
                return st;
            };
            _title = mk(19, true, TextAnchor.MiddleLeft);
            _sub = mk(15, false, TextAnchor.MiddleLeft);
            _small = mk(13, false, TextAnchor.MiddleLeft);
            _btn = mk(16, true, TextAnchor.MiddleCenter);
            _tabStyle = mk(16, true, TextAnchor.MiddleCenter);
            _big = mk(22, true, TextAnchor.MiddleCenter);
        }

        void OnGUI()
        {
            if (Event.current.type == EventType.KeyDown && _typing) { TypeKey(Event.current); return; }
            if (Event.current.type != EventType.Repaint) return;
            if (!_open && !_pillShown) return;
            Styles();
            _hitsNext.Clear();
            _origin = Vector2.zero; _clip = new Rect();
            if (!_open)
            {
                _pill = new Rect(_safe.x + 10f, _safe.y + 10f, 120f, 40f);
                Fill(_pill.x, _pill.y, _pill.width, _pill.height, new Color(0.1f, 0.1f, 0.15f, 0.75f));
                Fill(_pill.x, _pill.yMax - 3f, _pill.width, 3f, Accent);
                Text(_pill.x, _pill.y, _pill.width, _pill.height, "SONGS +", _btn, Color.white);
                return;
            }
            float W = Screen.width / _s, H = Screen.height / _s;
            float x0 = _safe.x + 8f, x1 = _safe.xMax - 8f, cw = x1 - x0;
            Fill(0, 0, W, H, Bg);

            // header: close + tabs
            Button(x0, 8f, 48f, 44f, "X", Bad, Close);
            string[] names = { "Search", "Top rated", "Latest", "Curated", "Ranked", "My songs" };
            float tx = x0 + 58f, tw = Mathf.Min(130f, (cw - 58f) / names.Length - 4f);
            for (int i = 0; i < names.Length; i++)
            {
                var t = (BsTab)i;
                Button(tx + i * (tw + 4f), 8f, tw, 44f, names[i], _tab == t ? Accent : Panel, () => SetTab(t));
            }

            // second row: search box / filter / status
            float y2 = 60f;
            if (_tab == BsTab.Search)
            {
                string shown = string.IsNullOrEmpty(_query) ? "Tap to search BeatSaber maps (song, artist, mapper or !bsr key)" : _query;
                Button(x0, y2, cw * 0.62f, 44f, "", Panel, EditQuery);
                Text(x0 + 12f, y2, cw * 0.62f - 24f, 44f, shown + (_typing ? "_" : ""), _sub,
                     string.IsNullOrEmpty(_query) ? Dim : Color.white);
            }
            else if (_tab == BsTab.Mine)
                Text(x0, y2, cw * 0.62f, 44f, "Installed songs (they show up under Solo › Custom Songs)", _sub, Dim);
            else
                Text(x0, y2, cw * 0.62f, 44f, "Browse BeatSaver — tap Get to install, the song appears in Solo › Custom Songs", _sub, Dim);
            if (_tab != BsTab.Mine)
                Button(x0 + cw * 0.64f, y2, cw * 0.36f, 44f, _hideModMaps ? "Hiding maps that need mods" : "Showing all maps",
                       _hideModMaps ? Panel : new Color(0.35f, 0.25f, 0.1f, 1f), () => { _hideModMaps = !_hideModMaps; if (_tab != BsTab.Search || !string.IsNullOrEmpty(_query)) Reload(); });

            // list
            _listRect = new Rect(x0, TopH, cw, H - TopH - StatusH);
            GUI.BeginGroup(R(_listRect.x, _listRect.y, _listRect.width, _listRect.height));
            _origin = new Vector2(_listRect.x, _listRect.y); _clip = _listRect;
            int count = _tab == BsTab.Mine ? _local.Count : _maps.Count;
            _maxScroll = Mathf.Max(0f, count * RowH + (_loading ? RowH : 0f) - _listRect.height);
            int first = Mathf.Max(0, Mathf.FloorToInt(_scroll / RowH)), last = Mathf.Min(count - 1, Mathf.CeilToInt((_scroll + _listRect.height) / RowH));
            for (int i = first; i <= last; i++)
            {
                float y = i * RowH - _scroll;
                if (_tab == BsTab.Mine) LocalRow(_local[i], y, cw); else MapRow(_maps[i], y, cw);
            }
            if (count == 0 || _loading)
                Text(0, count * RowH - _scroll + 10f, cw, 40f, _loading ? "Loading…" : (_tab == BsTab.Mine ? "No custom songs yet." : _status), _big, Dim);
            GUI.EndGroup();
            _origin = Vector2.zero; _clip = new Rect();

            // status bar
            Fill(0, H - StatusH, W, StatusH, new Color(0, 0, 0, 0.6f));
            int busy = _activeDownloads + _queue.Count;
            Text(x0, H - StatusH, cw, StatusH, (busy > 0 ? "Downloading " + busy + "…   " : "") + _status, _small, Dim);

            _hits.Clear(); _hits.AddRange(_hitsNext);
        }

        void MapRow(BsMap m, float y, float cw)
        {
            var v = m.Latest;
            var md = m.metadata;
            Fill(0, y + 2f, cw, RowH - 4f, Panel);
            Texture2D cover;
            if (_covers.TryGetValue(m.id, out cover) && cover != null) GUI.DrawTexture(R(6f, y + 7f, 64f, 64f), cover, ScaleMode.ScaleAndCrop);
            else { Fill(6f, y + 7f, 64f, 64f, new Color(0.2f, 0.2f, 0.26f)); WantCover(m.id, v.coverURL, false); }
            float tx = 80f, tw = cw - tx - 230f;
            string title = md.songName + (string.IsNullOrEmpty(md.songSubName) ? "" : "  " + md.songSubName);
            Text(tx, y + 6f, tw, 24f, title, _title, Color.white);
            Text(tx, y + 30f, tw, 20f, md.songAuthorName + "   ·   mapped by " + md.levelAuthorName, _sub, Dim);
            int votes = m.stats.upvotes + m.stats.downvotes;
            string info = string.Format("{0}:{1:00}   {2:0} BPM   {3}   {4}", md.duration / 60, md.duration % 60, md.bpm,
                                        votes > 0 ? Mathf.RoundToInt(m.stats.score * 100f) + "% of " + votes : "no votes", m.DiffSummary);
            if (m.ranked || m.blRanked) info += "   ranked";
            Text(tx, y + 52f, tw, 20f, info, _small, Dim);
            int modDiffs = m.ModDiffs;
            if (modDiffs > 0)
                Text(tx + tw - 190f, y + 52f, 190f, 20f, m.AllNeedMods ? "needs mods, won't play right" : "some difficulties need mods", _small, Warn);

            float bx = cw - 222f;
            Button(bx, y + 17f, 96f, 44f, _previewKey == m.id ? "Stop" : "Preview", _previewKey == m.id ? Warn : new Color(0.25f, 0.25f, 0.32f), () => TogglePreview(m));
            Download d;
            _downloads.TryGetValue(m.id, out d);
            bool have = _installed.Contains(m.id.ToLowerInvariant());
            if (d != null && d.state == "downloading")
                Button(bx + 104f, y + 17f, 112f, 44f, Mathf.RoundToInt(d.progress * 100f) + "%", Accent, null);
            else if (d != null && (d.state == "queued" || d.state == "installing"))
                Button(bx + 104f, y + 17f, 112f, 44f, d.state == "queued" ? "Queued" : "Installing", Accent, null);
            else if (d != null && d.state == "failed")
            {
                Button(bx + 104f, y + 17f, 112f, 44f, "Retry", Bad, () => Get(m));
                Text(tx, y + 52f, tw, 20f, d.message, _small, Warn);
            }
            else if (have)
                Button(bx + 104f, y + 17f, 112f, 44f, "Installed", Good, null);
            else
                Button(bx + 104f, y + 17f, 112f, 44f, "Get", Accent, () => Get(m));
        }

        void LocalRow(Songs.Local l, float y, float cw)
        {
            Fill(0, y + 2f, cw, RowH - 4f, Panel);
            Texture2D cover;
            if (_covers.TryGetValue(l.path, out cover) && cover != null) GUI.DrawTexture(R(6f, y + 7f, 64f, 64f), cover, ScaleMode.ScaleAndCrop);
            else { Fill(6f, y + 7f, 64f, 64f, new Color(0.2f, 0.2f, 0.26f)); WantCover(l.path, l.cover, true); }
            float tx = 80f, tw = cw - tx - 140f;
            Text(tx, y + 8f, tw, 24f, l.title, _title, Color.white);
            Text(tx, y + 34f, tw, 20f, (l.artist ?? "") + (string.IsNullOrEmpty(l.mapper) ? "" : "   ·   mapped by " + l.mapper), _sub, Dim);
            Text(tx, y + 54f, tw, 18f, l.folder, _small, new Color(0.5f, 0.5f, 0.58f));
            bool confirm = _confirmDelete == l.path && Time.unscaledTime < _confirmUntil;
            Button(cw - 130f, y + 17f, 124f, 44f, confirm ? "Tap to confirm" : "Delete", confirm ? Bad : new Color(0.3f, 0.2f, 0.22f), () => DeleteLocal(l));
        }

        void TypeKey(Event e)
        {
            if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter) { _typing = false; _tab = BsTab.Search; Reload(); }
            else if (e.keyCode == KeyCode.Escape) _typing = false;
            else if (e.keyCode == KeyCode.Backspace) { if (_query.Length > 0) _query = _query.Substring(0, _query.Length - 1); }
            else if (e.character >= ' ') _query += e.character;
            e.Use();
        }
    }
}
