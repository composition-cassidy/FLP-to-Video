/*
 * FLP to Video  v1.0
 * FL Studio project -> video placement for VEGAS Pro 16 and newer.
 *
 * The FL Studio sibling of MIDI to Video: instead of a MIDI export it reads the .flp itself
 * (through flp2json.exe, built on PyFLP), so it knows what MIDI loses:
 *   - step-sequencer hits get a real length instead of "held until the song ends"
 *   - slide notes don't start a new clip (optionally they get a zoom pop)
 *   - audio clips chopped in the playlist are cut from the same spot of the source clip
 *   - tempo automation, muted tracks/clips and multiple arrangements
 *
 * SETUP
 *   Keep this file and flp2json.exe together in VEGAS's "Script Menu" folder (a subfolder is fine).
 *   VEGAS Pro 13 or older: change "using ScriptPortal.Vegas;" below to "using Sony.Vegas;".
 *
 * USE
 *   1. Click the clip to place on the timeline (or select a file in Project Media).
 *      For chopped audio clips, select the same event you rendered the sample from.
 *   2. Tools > Scripting > FLP to Video, then pick the .flp.
 *   3. Tick channels, set timing and effects, press Generate. One Ctrl+Z undoes the whole run.
 *
 * Settings and effect presets are kept in %APPDATA%\FLP to Video\.
 */

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using ScriptPortal.Vegas;

namespace FlpToVideo
{
    // =========================================================================================
    //  Entry point
    // =========================================================================================

    public class EntryPoint
    {
        public void FromVegas(Vegas vegas)
        {
            try
            {
                Theme.Init();
                Run(vegas);
            }
            catch (UserError ex) { Ui.Warn(null, ex.Message); }
            catch (Exception ex) { Ui.Crash(ex); }
        }

        static void Run(Vegas vegas)
        {
            Settings st = Settings.Load();

            SourceClip source = SourceClip.Find(vegas);   // the clip selected on the timeline, if any
            Subclips subclips = Subclips.Scan(vegas);
            if (source == null && subclips.Count == 0)
                throw new UserError("Select the clip to place first.\n\nClick a video clip on the timeline, or name " +
                    "Project Media subclips after your FL channels or samples, then run FLP to Video again.");

            // Reopen the last project (re-read, so FL edits since last time come along); ask only when there's none.
            IWin32Window owner = Native.VegasWindow();
            string path = st.LastFlp;
            FlpProject flp = null;
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                try { flp = FlpProject.Load(path); }
                catch (UserError ex) { Ui.Warn(owner, ex.Message); }
            }
            if (flp == null)
            {
                path = Ui.PickFlp(owner, st.LastFlp);
                if (path == null) return;
                flp = FlpProject.Load(path);
            }

            bool sameFile = string.Equals(st.LastFlp, path, StringComparison.OrdinalIgnoreCase);
            st.LastFlp = path;

            // the pose editors draw on the project frame, with a real frame of the clip when VEGAS can render one
            try { FrameRef.Width = vegas.Project.Video.Width; FrameRef.Height = vegas.Project.Video.Height; } catch { }
            FrameRef.Loader = delegate { return Snapshot(vegas, source); };

            Job job = null;
            using (MainForm form = new MainForm(vegas, source, subclips, flp, st, sameFile))
            {
                if (form.ShowDialog(owner) == DialogResult.OK) job = form.BuildJob();
                form.SaveTo(st);
            }
            st.Save();
            if (job != null) Generator.Run(vegas, job);
        }

        /// <summary>A frame from the middle of the selected clip, as VEGAS renders it (null if it can't).</summary>
        static Image Snapshot(Vegas vegas, SourceClip src)
        {
            if (src == null || src.Event == null) return null;
            string path = Path.Combine(Path.GetTempPath(), "flp2vid_frame_" + Guid.NewGuid().ToString("N") + ".png");
            try
            {
                Timecode at = src.Event.Start + Timecode.FromMilliseconds(Math.Min(src.LengthMs / 2, 250));
                if (vegas.SaveSnapshot(path, ImageFileFormat.PNG, at) != RenderStatus.Complete || !File.Exists(path)) return null;
                using (MemoryStream ms = new MemoryStream(File.ReadAllBytes(path)))
                using (Image img = Image.FromStream(ms))
                    return new Bitmap(img);   // detached from the stream and the temp file
            }
            catch { return null; }
            finally { try { if (File.Exists(path)) File.Delete(path); } catch { } }
        }
    }

    internal sealed class UserError : Exception
    {
        public UserError(string message) : base(message) { }
    }

    internal static class Ui
    {
        public const string App = "FLP to Video";

        public static string PickFlp(IWin32Window owner, string last)
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Title = "Open FL Studio project";
                d.Filter = "FL Studio projects (*.flp)|*.flp|All files (*.*)|*.*";
                try
                {
                    if (!string.IsNullOrEmpty(last))
                    {
                        string dir = Path.GetDirectoryName(last);
                        if (Directory.Exists(dir)) d.InitialDirectory = dir;
                        if (File.Exists(last)) d.FileName = Path.GetFileName(last);
                    }
                }
                catch { }
                return d.ShowDialog(owner) == DialogResult.OK ? d.FileName : null;
            }
        }

        public static void Warn(IWin32Window owner, string text)
        {
            MessageBox.Show(owner, text, App, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        public static bool Confirm(IWin32Window owner, string text)
        {
            return MessageBox.Show(owner, text, App, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
        }

        public static void Crash(Exception ex)
        {
            string[] lines = (ex.StackTrace ?? "").Split('\n');
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < lines.Length && i < 5; i++) sb.AppendLine(lines[i].Trim());
            MessageBox.Show("Something went wrong:\n\n" + ex.Message + "\n\n" + ex.GetType().Name + "\n" + sb,
                App, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // =========================================================================================
    //  What to generate
    // =========================================================================================

    internal sealed class Job
    {
        public Song Song;
        public readonly List<Part> Parts = new List<Part>();
        public double StartMs;            // timeline position of song time zero
        public bool NewTracks = true;     // false = everything goes on Target
        public VideoTrack Target;
        public int MinLen, MaxLen;        // indexes into Planner.LengthNames (0 = off)
        public int StepLen;               // index into Planner.StepNames
        public int SlideMode;             // 0 continue, 1 pop, 2 toggle, 3 new clip
        public Pose SlidePose = new Pose(112, 0, 0, 0); // where a slide sends the picture
        public double SlideEaseMs = 150;  // pop: settles back over this; toggle: glides over this
        public bool IncludeMuted;         // muted playlist tracks / clips
        public bool Audio;                // also copy the clip's audio
        public int PitchMode;             // 0 off, 1 shift the audio, 2 resample (speed follows pitch)
        public int Root = 60;             // FL key that plays the clip as-is (60 = C5)
        public int LongMode;              // 0 extend, 1 freeze, 2 stretch
        public bool Velocity = true;      // velocity -> opacity
        public double Scale = 1;          // tempo override (time multiplier)
        public readonly List<EffectSpec> Effects = new List<EffectSpec>();
        public SourceClip Default;        // the clip selected on the timeline (may be null)
        public readonly Dictionary<Part, SourceClip> Sources = new Dictionary<Part, SourceClip>(); // subclip matches

        public EffectSpec Get(Fx type)
        {
            foreach (EffectSpec e in Effects) if (e.Type == type) return e;
            return null;
        }

        public SourceClip SourceFor(Part part)
        {
            SourceClip s;
            return Sources.TryGetValue(part, out s) ? s : Default;
        }
    }

    internal sealed class SourceClip
    {
        public VideoEvent Event;          // timeline clip (copied per note), or
        public Media Media;               // Project Media subclip (new event per note)
        public Track Track;
        public AudioEvent AudioEvent;     // audio grouped with Event (same start), if any
        public string Name = "", From = "";
        public double LengthMs, Rate = 1;
        public float Gain = 1;
        public int SelectedCount = 1;

        public double MediaSpanMs { get { return LengthMs * Rate; } }

        public bool HasAudio
        {
            get
            {
                if (AudioEvent != null) return true;
                try { return Media != null && Media.HasAudio(); } catch { return false; }
            }
        }

        public static SourceClip FromMedia(Media m, string name)
        {
            SourceClip s = new SourceClip();
            s.Media = m;
            s.Name = name;
            s.From = "subclip";
            s.LengthMs = m.Length.ToMilliseconds();
            Subclip sc = m as Subclip;
            try
            {
                // some VEGAS versions report the parent's length for a subclip
                if (sc != null && sc.ParentMedia != null && Math.Abs(s.LengthMs - sc.ParentMedia.Length.ToMilliseconds()) < 1)
                    s.LengthMs = Math.Max(1, s.LengthMs - sc.Start.ToMilliseconds());
            }
            catch { }
            return s;
        }

        /// <summary>Only the clip selected on the timeline; Project Media selection is ignored.</summary>
        public static SourceClip Find(Vegas vegas)
        {
            SourceClip found = null;
            int count = 0;
            foreach (Track t in vegas.Project.Tracks)
            {
                if (!t.IsVideo()) continue;
                foreach (TrackEvent e in t.Events)
                {
                    VideoEvent ve = e as VideoEvent;
                    if (!e.Selected || ve == null || ve.ActiveTake == null) continue;
                    count++;
                    if (found != null) continue;
                    found = new SourceClip();
                    found.Event = ve;
                    found.Track = t;
                    found.Name = ve.ActiveTake.Name;
                    if (string.IsNullOrEmpty(found.Name) && ve.ActiveTake.Media != null)
                        found.Name = FileName(ve.ActiveTake.Media.FilePath);
                    found.From = "timeline";
                    found.LengthMs = ve.Length.ToMilliseconds();
                    found.Rate = ve.PlaybackRate > 0 ? ve.PlaybackRate : 1;
                    found.Gain = ve.FadeIn.Gain;
                    found.AudioEvent = GroupedAudio(ve);
                }
            }
            if (found != null) found.SelectedCount = count;
            return found;
        }

        public VideoEvent Create(VideoTrack track, Timecode start, Timecode length)
        {
            VideoEvent ev;
            if (Event != null)
            {
                ev = (VideoEvent)Event.Copy(track, start);
                ev.Length = length;
            }
            else
            {
                ev = track.AddVideoEvent(start, length);
                ev.AddTake(Media.GetVideoStreamByIndex(0));
            }
            return ev;
        }

        /// <summary>The matching audio for a new video copy (same start, length and take offset).</summary>
        public AudioEvent CreateAudio(AudioTrack track, VideoEvent video)
        {
            AudioEvent ev;
            if (AudioEvent != null)
            {
                ev = (AudioEvent)AudioEvent.Copy(track, video.Start);
                ev.Length = video.Length;
            }
            else
            {
                ev = track.AddAudioEvent(video.Start, video.Length);
                ev.AddTake(Media.GetAudioStreamByIndex(0));
            }
            // speed first: VEGAS rescales the take offset when the rate changes
            if (Math.Abs(video.PlaybackRate - ev.PlaybackRate) > 1e-6) Generator.SetRate(ev, video.PlaybackRate, video.Length);
            if (ev.ActiveTake != null && video.ActiveTake != null)
            {
                if (AudioEvent != null && AudioEvent.ActiveTake != null && Event != null && Event.ActiveTake != null)
                    ev.ActiveTake.Offset = AudioEvent.ActiveTake.Offset + (video.ActiveTake.Offset - Event.ActiveTake.Offset);
                else if (AudioEvent == null)
                    ev.ActiveTake.Offset = video.ActiveTake.Offset;
            }
            ev.Selected = false;
            ev.FadeIn.Length = Timecode.FromMilliseconds(0);
            ev.FadeOut.Length = Timecode.FromMilliseconds(0);
            return ev;
        }

        static AudioEvent GroupedAudio(VideoEvent ve)
        {
            try
            {
                if (!ve.IsGrouped || ve.Group == null) return null;
                foreach (TrackEvent e in ve.Group)
                {
                    AudioEvent ae = e as AudioEvent;
                    if (ae != null && ae.ActiveTake != null && Math.Abs(ae.Start.ToMilliseconds() - ve.Start.ToMilliseconds()) < 1) return ae;
                }
            }
            catch { }
            return null;
        }

        static string FileName(string path)
        {
            try { return Path.GetFileName(path); } catch { return path ?? ""; }
        }
    }

    /// <summary>
    /// Project Media subclips, findable by name. A subclip named like an FL channel or its sample file
    /// ("i am bout to try") becomes that channel's clip, so many channels can be placed in one run.
    /// </summary>
    internal sealed class Subclips
    {
        readonly Dictionary<string, SourceClip> byName = new Dictionary<string, SourceClip>();
        static readonly string[] MediaExt = { ".wav", ".mp3", ".flac", ".ogg", ".aif", ".aiff", ".mp4", ".mov", ".mkv", ".avi", ".webm", ".m4a", ".wmv" };

        public int Count { get { return byName.Count; } }

        public static Subclips Scan(Vegas vegas)
        {
            Subclips s = new Subclips();
            try
            {
                IDictionaryEnumerator en = vegas.Project.MediaPool.GetEnumerator();
                while (en.MoveNext())
                {
                    Subclip m = en.Value as Subclip;
                    if (m == null) continue;
                    try { if (!m.HasVideo()) continue; } catch { continue; }
                    List<string> names = new List<string>();
                    try { names.Add(m.Title); } catch { }
                    names.Add(en.Key as string);
                    try { names.Add(m.FilePath); } catch { }
                    string shown = null;
                    foreach (string n in names)
                    {
                        string k = Key(n);
                        if (k.Length == 0) continue;
                        if (shown == null) { shown = n.Trim(); try { shown = Path.GetFileName(shown); } catch { } }
                        if (!s.byName.ContainsKey(k)) s.byName[k] = SourceClip.FromMedia(m, shown);
                    }
                }
            }
            catch { }
            return s;
        }

        /// <summary>The subclip named after this channel or its sample, or null.</summary>
        public SourceClip Match(Part part)
        {
            SourceClip c;
            if (byName.TryGetValue(Key(part.Name), out c)) return c;
            if (byName.TryGetValue(Key(part.SampleName), out c)) return c;
            return null;
        }

        static string Key(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            string n = name.Trim();
            try { n = Path.GetFileName(n); } catch { }
            foreach (string ext in MediaExt)
                if (n.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) { n = n.Substring(0, n.Length - ext.Length); break; }
            StringBuilder b = new StringBuilder();
            bool space = false;
            foreach (char ch in n.ToLowerInvariant())
            {
                if (char.IsWhiteSpace(ch) || ch == '_') { space = b.Length > 0; continue; }
                if (space) { b.Append(' '); space = false; }
                b.Append(ch);
            }
            return b.ToString();
        }
    }

    // =========================================================================================
    //  FL Studio project reading (flp2json.exe does the parsing; this reads its JSON)
    // =========================================================================================

    internal sealed class Note
    {
        public const int Step = 1, Slide = 2, Muted = 4;
        public long Start, End;
        public int Key, Vel, Flags;
        public double SrcMs = -1, SrcEndMs = -1;   // audio clips: span of the sample (ms), -1 = untrimmed

        public bool Is(int flag) { return (Flags & flag) != 0; }
    }

    /// <summary>One FL channel: its piano-roll / step notes, or its audio clips in the playlist.</summary>
    internal sealed class Part
    {
        public int Channel;
        public bool Clips;
        public string Name = "", SampleName = "";
        public double SampleMs = -1;
        public int MaxVel = 1;
        public int Hits;                  // distinct starts (chords count once), muted excluded
        public int MutedHits;
        public bool HasSteps, HasSlides;
        public readonly List<Note> Notes = new List<Note>();

        public string Id { get { return Channel + ":" + (Clips ? "clips" : "notes"); } }
        public string ChannelText { get { return Clips ? "Clips" : "Ch " + (Channel + 1); } }
        public string Label { get { return ChannelText + " · " + Name; } }

        public string Extra
        {
            get
            {
                List<string> l = new List<string>();
                if (HasSteps) l.Add("steps");
                if (HasSlides) l.Add("slides");
                if (MutedHits > 0) l.Add(MutedHits + " muted");
                return string.Join(" · ", l.ToArray());
            }
        }

        public int CountHits(bool includeMuted)
        {
            int n = 0;
            long last = -1;
            foreach (Note x in Notes)
            {
                if (x.Is(Note.Muted) && !includeMuted) continue;
                if (!Clips && x.Is(Note.Slide)) continue;
                if (x.Start != last) { n++; last = x.Start; }
            }
            return n;
        }
    }

    internal sealed class TempoMap
    {
        readonly long[] ticks;
        readonly double[] usPerQuarter, msAt;
        readonly int ppq;

        /// <summary>changes: [tick, BPM] sorted by tick, first one at tick 0.</summary>
        public TempoMap(List<double[]> changes, int ppq)
        {
            this.ppq = Math.Max(1, ppq);
            List<long> tk = new List<long>();
            List<double> us = new List<double>();
            tk.Add(0);
            us.Add(500000);
            foreach (double[] c in changes)
            {
                if (c[1] <= 0) continue;
                long t = Math.Max(0, (long)c[0]);
                double u = 60000000.0 / c[1];
                if (t == tk[tk.Count - 1]) us[us.Count - 1] = u;
                else if (t > tk[tk.Count - 1]) { tk.Add(t); us.Add(u); }
            }
            ticks = tk.ToArray();
            usPerQuarter = us.ToArray();
            msAt = new double[ticks.Length];
            for (int i = 1; i < ticks.Length; i++)
                msAt[i] = msAt[i - 1] + (ticks[i] - ticks[i - 1]) * usPerQuarter[i - 1] / 1000.0 / this.ppq;
        }

        public double FirstBpm { get { return 60000000.0 / usPerQuarter[0]; } }
        public bool Varies { get { return ticks.Length > 1; } }

        public double TickToMs(long tick)
        {
            int i = Find(tick);
            return msAt[i] + (tick - ticks[i]) * usPerQuarter[i] / 1000.0 / ppq;
        }

        public long MsToTick(double ms)
        {
            int lo = 0, hi = ticks.Length - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (msAt[mid] <= ms) lo = mid; else hi = mid - 1;
            }
            return ticks[lo] + (long)Math.Round((ms - msAt[lo]) * 1000.0 * ppq / usPerQuarter[lo]);
        }

        int Find(long tick)
        {
            int lo = 0, hi = ticks.Length - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (ticks[mid] <= tick) lo = mid; else hi = mid - 1;
            }
            return lo;
        }
    }

    /// <summary>One arrangement of the project, ready to place.</summary>
    internal sealed class Song
    {
        public string Path = "", FileName = "", Arrangement = "";
        public int Ppq = 96;
        public TempoMap Tempo;
        public double Bpm = 120;
        public bool TempoVaries;
        public readonly List<string> MutedTracks = new List<string>();
        public int MutedClips;
        public readonly List<Part> Parts = new List<Part>();

        public double QuarterTicks { get { return Ppq; } }
        public bool HasMuted { get { return MutedTracks.Count > 0 || MutedClips > 0; } }
    }

    internal sealed class FlpProject
    {
        public string Path = "", FileName = "", Version = "";
        public readonly List<Song> Arrangements = new List<Song>();
        public int Current;

        public static FlpProject Load(string path)
        {
            string exe = FindTool();
            if (exe == null)
                throw new UserError("flp2json.exe wasn't found.\n\nKeep it in the same folder as this script:\n" + ScriptDir());

            string json = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "flp2vid_" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(exe, Quote(path) + " " + Quote(json));
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardError = true;
                psi.RedirectStandardOutput = true;
                string err;
                int code;
                Cursor old = Cursor.Current;
                Cursor.Current = Cursors.WaitCursor;
                try
                {
                    using (Process p = Process.Start(psi))
                    {
                        p.StandardOutput.ReadToEndAsync();
                        err = p.StandardError.ReadToEnd();
                        if (!p.WaitForExit(120000)) { try { p.Kill(); } catch { } throw new UserError("Reading the project took too long."); }
                        code = p.ExitCode;
                    }
                }
                finally { Cursor.Current = old; }
                if (code != 0 || !File.Exists(json))
                    throw new UserError("Couldn't read \"" + System.IO.Path.GetFileName(path) + "\".\n\n" + (err ?? "").Trim());
                return FromJson(path, Json.Parse(File.ReadAllText(json, Encoding.UTF8)));
            }
            finally
            {
                try { if (File.Exists(json)) File.Delete(json); } catch { }
            }
        }

        static string Quote(string s) { return "\"" + s.Replace("\"", "\\\"") + "\""; }

        static string ScriptDir()
        {
            try { return System.IO.Path.GetDirectoryName(Script.File); } catch { return ""; }
        }

        static string FindTool()
        {
            List<string> dirs = new List<string>();
            dirs.Add(ScriptDir());
            dirs.Add(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FLP to Video"));
            dirs.Add(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Vegas Script Menu\\FLP TO VIDEO"));
            foreach (string d in dirs)
            {
                try
                {
                    string f = System.IO.Path.Combine(d ?? "", "flp2json.exe");
                    if (File.Exists(f)) return f;
                }
                catch { }
            }
            return null;
        }

        static FlpProject FromJson(string path, object root)
        {
            Dictionary<string, object> d = root as Dictionary<string, object>;
            if (d == null) throw new UserError("flp2json.exe returned something unexpected.");
            FlpProject f = new FlpProject();
            f.Path = path;
            f.FileName = System.IO.Path.GetFileName(path);
            f.Version = Json.Str(d, "version");
            int ppq = Math.Max(1, (int)Json.Num(d, "ppq", 96));

            foreach (object ao in Json.List(d, "arrangements"))
            {
                Dictionary<string, object> a = ao as Dictionary<string, object>;
                if (a == null) continue;
                Song s = new Song();
                s.Path = path;
                s.FileName = f.FileName;
                s.Arrangement = Json.Str(a, "name");
                s.Ppq = ppq;
                List<double[]> tempo = new List<double[]>();
                foreach (object t in Json.List(a, "tempo"))
                {
                    List<object> pair = t as List<object>;
                    if (pair != null && pair.Count >= 2) tempo.Add(new double[] { Json.ToNum(pair[0]), Json.ToNum(pair[1]) });
                }
                if (tempo.Count == 0) tempo.Add(new double[] { 0, Json.Num(d, "bpm", 120) });
                s.Tempo = new TempoMap(tempo, ppq);
                s.Bpm = s.Tempo.FirstBpm;
                s.TempoVaries = s.Tempo.Varies;
                foreach (object m in Json.List(a, "mutedTracks")) s.MutedTracks.Add(m as string ?? "");
                s.MutedClips = (int)Json.Num(a, "mutedClips", 0);

                foreach (object po in Json.List(a, "parts"))
                {
                    Dictionary<string, object> pd = po as Dictionary<string, object>;
                    if (pd == null) continue;
                    Part part = new Part();
                    part.Channel = (int)Json.Num(pd, "channel", 0);
                    part.Clips = Json.Str(pd, "kind") == "clips";
                    part.Name = Clean(Json.Str(pd, "name"));
                    if (part.Name.Length == 0) part.Name = "Channel " + (part.Channel + 1);
                    part.SampleMs = Json.Num(pd, "sampleMs", -1);
                    try { part.SampleName = System.IO.Path.GetFileNameWithoutExtension(Json.Str(pd, "sample")); } catch { }
                    foreach (object io in Json.List(pd, "items"))
                    {
                        List<object> it = io as List<object>;
                        if (it == null || it.Count < 5) continue;
                        Note n = new Note();
                        n.Start = (long)Json.ToNum(it[0]);
                        n.End = Math.Max(n.Start, (long)Json.ToNum(it[1]));
                        n.Flags = (int)Json.ToNum(it[4]);
                        if (part.Clips)
                        {
                            n.SrcMs = Json.ToNum(it[2]);
                            n.SrcEndMs = Json.ToNum(it[3]);
                            if (n.SrcEndMs < 0 && part.SampleMs > n.SrcMs) n.SrcEndMs = part.SampleMs; // untrimmed = whole sample
                            n.Vel = 100;
                        }
                        else
                        {
                            n.Key = (int)Json.ToNum(it[2]);
                            n.Vel = (int)Json.ToNum(it[3]);
                            if (n.Is(Note.Step)) part.HasSteps = true;
                            if (n.Is(Note.Slide)) part.HasSlides = true;
                        }
                        if (n.Vel > part.MaxVel) part.MaxVel = n.Vel;
                        part.Notes.Add(n);
                    }
                    part.Notes.Sort(delegate (Note x, Note y) { int c = x.Start.CompareTo(y.Start); return c != 0 ? c : x.End.CompareTo(y.End); });
                    part.Hits = part.CountHits(false);
                    part.MutedHits = part.CountHits(true) - part.Hits;
                    if (part.Notes.Count > 0) s.Parts.Add(part);
                }
                if (Json.Bool(a, "current")) f.Current = f.Arrangements.Count;
                f.Arrangements.Add(s);
            }
            if (f.Arrangements.Count == 0) throw new UserError("\"" + f.FileName + "\" has no playlist arrangement.");
            bool any = false;
            foreach (Song s in f.Arrangements) if (s.Parts.Count > 0) any = true;
            if (!any) throw new UserError("\"" + f.FileName + "\" has nothing in its playlist to place.");
            return f;
        }

        static string Clean(string s)
        {
            if (s == null) return "";
            StringBuilder b = new StringBuilder();
            foreach (char c in s) if (!char.IsControl(c)) b.Append(c);
            return b.ToString().Trim();
        }
    }

    /// <summary>Minimal JSON reader: objects -> Dictionary, arrays -> List, numbers -> double.</summary>
    internal static class Json
    {
        public static object Parse(string s)
        {
            int p = 0;
            object v = Value(s, ref p);
            return v;
        }

        public static string Str(Dictionary<string, object> d, string k)
        {
            object v;
            return d.TryGetValue(k, out v) && v is string ? (string)v : "";
        }

        public static double Num(Dictionary<string, object> d, string k, double fallback)
        {
            object v;
            return d.TryGetValue(k, out v) && v is double ? (double)v : fallback;
        }

        public static bool Bool(Dictionary<string, object> d, string k)
        {
            object v;
            return d.TryGetValue(k, out v) && v is bool && (bool)v;
        }

        public static List<object> List(Dictionary<string, object> d, string k)
        {
            object v;
            return d.TryGetValue(k, out v) && v is List<object> ? (List<object>)v : new List<object>();
        }

        public static double ToNum(object v) { return v is double ? (double)v : 0; }

        static void Ws(string s, ref int p) { while (p < s.Length && char.IsWhiteSpace(s[p])) p++; }

        static object Value(string s, ref int p)
        {
            Ws(s, ref p);
            if (p >= s.Length) throw new UserError("flp2json.exe returned incomplete data.");
            char c = s[p];
            if (c == '{')
            {
                Dictionary<string, object> d = new Dictionary<string, object>();
                p++;
                Ws(s, ref p);
                if (p < s.Length && s[p] == '}') { p++; return d; }
                while (p < s.Length)
                {
                    Ws(s, ref p);
                    string k = Text(s, ref p);
                    Ws(s, ref p);
                    p++; // ':'
                    d[k] = Value(s, ref p);
                    Ws(s, ref p);
                    if (p < s.Length && s[p] == ',') { p++; continue; }
                    p++; // '}'
                    break;
                }
                return d;
            }
            if (c == '[')
            {
                List<object> l = new List<object>();
                p++;
                Ws(s, ref p);
                if (p < s.Length && s[p] == ']') { p++; return l; }
                while (p < s.Length)
                {
                    l.Add(Value(s, ref p));
                    Ws(s, ref p);
                    if (p < s.Length && s[p] == ',') { p++; continue; }
                    p++; // ']'
                    break;
                }
                return l;
            }
            if (c == '"') return Text(s, ref p);
            if (string.CompareOrdinal(s, p, "true", 0, 4) == 0) { p += 4; return true; }
            if (string.CompareOrdinal(s, p, "false", 0, 5) == 0) { p += 5; return false; }
            if (string.CompareOrdinal(s, p, "null", 0, 4) == 0) { p += 4; return null; }
            if (string.CompareOrdinal(s, p, "NaN", 0, 3) == 0) { p += 3; return double.NaN; }
            int start = p;
            while (p < s.Length && "+-0123456789.eE".IndexOf(s[p]) >= 0) p++;
            double v;
            if (p == start || !double.TryParse(s.Substring(start, p - start), NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                throw new UserError("flp2json.exe returned data this script can't read.");
            return v;
        }

        static string Text(string s, ref int p)
        {
            StringBuilder b = new StringBuilder();
            p++; // opening quote
            while (p < s.Length && s[p] != '"')
            {
                char c = s[p++];
                if (c != '\\' || p >= s.Length) { b.Append(c); continue; }
                char e = s[p++];
                switch (e)
                {
                    case 'n': b.Append('\n'); break;
                    case 't': b.Append('\t'); break;
                    case 'r': b.Append('\r'); break;
                    case 'b': b.Append('\b'); break;
                    case 'f': b.Append('\f'); break;
                    case 'u':
                        if (p + 4 <= s.Length) { b.Append((char)Convert.ToInt32(s.Substring(p, 4), 16)); p += 4; }
                        break;
                    default: b.Append(e); break;
                }
            }
            p++; // closing quote
            return b.ToString();
        }
    }

    // =========================================================================================
    //  Notes -> placements (timing rules)
    // =========================================================================================

    internal sealed class Placement
    {
        public double StartMs, LenMs, Opacity;
        public int Index;                 // 0-based note number on its track (drives alternating effects)
        public double SrcMs;              // audio clips: where in the source clip to start (ms of the sample)
        public double Rate = 1;           // audio clips: speed relative to the source clip
        public SourceClip Source;         // which clip to copy (the channel's subclip or the selected clip)
        public int Semis;                 // pitch relative to the root note (0 = as recorded)
        public bool SlideOn;              // Toggle slides: is the slide pose on when this clip starts?
        public readonly List<double> SlideMs = new List<double>(); // slide starts, ms into the clip
    }

    internal static class Planner
    {
        public static readonly string[] LengthNames = { "Off", "1/64", "1/32", "1/16", "1/8", "1/4", "1/2", "1/1 (bar)" };
        static readonly double[] LengthWholeNotes = { 0, 1 / 64.0, 1 / 32.0, 1 / 16.0, 1 / 8.0, 1 / 4.0, 1 / 2.0, 1.0 };
        public static readonly string[] StepNames = { "Until next hit", "Sample length", "1/64", "1/32", "1/16", "1/8", "1/4", "1/2", "1/1 (bar)" };
        public static readonly string[] SlideNames = { "Continue", "Pop", "Toggle", "New clip" };
        public static readonly string[] PitchNames = { "Off", "Shift", "Resample" };
        public const int StepUntilNext = 0, StepSample = 1;
        public const int SlideContinue = 0, SlidePop = 1, SlideToggle = 2, SlideNewClip = 3;
        public const int PitchOff = 0, PitchShift = 1, PitchResample = 2;
        public const int RootLow = 24, RootHigh = 107;   // root choices: C2 .. B8 (FL numbering, 60 = C5)
        static readonly string[] NoteLetters = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

        public static string KeyName(int key) { return NoteLetters[((key % 12) + 12) % 12] + (key / 12); }

        public static string[] RootNames()
        {
            List<string> l = new List<string>();
            for (int k = RootLow; k <= RootHigh; k++) l.Add(KeyName(k));
            return l.ToArray();
        }

        static long LengthTicks(int index, double quarterTicks)
        {
            if (index <= 0 || index >= LengthWholeNotes.Length) return 0;
            return Math.Max(1, (long)Math.Round(LengthWholeNotes[index] * 4 * quarterTicks));
        }

        sealed class Hit
        {
            public long Start, End;
            public int Vel, Key;
            public bool Step;
            public double SrcMs, SrcEndMs = -1;
            public readonly List<long> Slides = new List<long>();
        }

        /// <summary>
        /// One placement per note start. Chords collapse to one clip (loudest velocity, longest length),
        /// min/max length is applied in note values, and every clip is cut where the next one starts.
        /// Step-sequencer hits take their length from the step setting; slide notes extend the clip
        /// they glide from instead of starting a new one. Audio clips keep their exact playlist span.
        /// Opacity = velocity / loudest velocity in this channel.
        /// </summary>
        public static List<Placement> Build(Song song, Part part, Job job)
        {
            double q = song.QuarterTicks, scale = job.Scale;
            long minT = LengthTicks(job.MinLen, q), maxT = LengthTicks(job.MaxLen, q);
            bool slidesMerge = job.SlideMode != SlideNewClip;

            List<Hit> hits = new List<Hit>();
            List<Note> slideNotes = new List<Note>();
            foreach (Note n in part.Notes)
            {
                if (n.Is(Note.Muted) && !job.IncludeMuted) continue;
                if (!part.Clips && n.Is(Note.Slide) && slidesMerge) { slideNotes.Add(n); continue; }
                Hit last = hits.Count > 0 ? hits[hits.Count - 1] : null;
                if (last != null && last.Start == n.Start)
                {
                    if (n.End > last.End) last.End = n.End;
                    if (n.Vel > last.Vel) { last.Vel = n.Vel; last.Key = n.Key; } // a chord pitches to its loudest note
                    last.Step = last.Step && n.Is(Note.Step);
                    continue;
                }
                Hit h = new Hit();
                h.Start = n.Start;
                h.End = n.End;
                h.Vel = n.Vel;
                h.Key = n.Key;
                h.Step = !part.Clips && n.Is(Note.Step);
                h.SrcMs = Math.Max(0, n.SrcMs);
                h.SrcEndMs = n.SrcEndMs;
                hits.Add(h);
            }

            // A slide bends the note sounding when it starts (one starting on the same tick counts).
            // The cue goes where the slide starts, or where its glide lands if it starts with the note.
            foreach (Note n in slideNotes)
            {
                Hit owner = null;
                foreach (Hit h in hits)
                {
                    if (h.Start > n.Start) break;
                    owner = h;
                }
                if (owner == null) continue;
                if (n.End > owner.End) owner.End = n.End;
                owner.Slides.Add(n.Start > owner.Start ? n.Start : n.End);
            }

            List<Placement> result = new List<Placement>(hits.Count);
            bool toggled = false;
            for (int i = 0; i < hits.Count; i++)
            {
                Hit h = hits[i];
                long start = h.Start, end = h.End;
                bool hasNext = i + 1 < hits.Count;
                long next = hasNext ? hits[i + 1].Start : long.MaxValue;
                double sampleMs = -1;     // a fixed length in real ms (step hits set to "Sample length")

                if (h.Step)
                {
                    if (job.StepLen == StepUntilNext)
                    {
                        if (hasNext) end = next;
                        else if (part.SampleMs > 0) sampleMs = part.SampleMs;
                        else end = start + LengthTicks(5, q);
                    }
                    else if (job.StepLen == StepSample && part.SampleMs > 0) sampleMs = part.SampleMs;
                    else end = start + Math.Max(LengthTicks(job.StepLen >= 2 ? job.StepLen - 1 : 3, q), 1);
                }
                else if (!part.Clips)
                {
                    long len = Math.Max(1, end - start);
                    if (minT > 0 && len < minT) len = minT;
                    if (maxT > 0 && len > maxT) len = maxT;
                    end = start + len;
                }
                if (end <= start) end = start + 1;

                Placement pl = new Placement();
                pl.StartMs = song.Tempo.TickToMs(start) * scale;
                double endMs = sampleMs > 0 ? pl.StartMs + sampleMs : song.Tempo.TickToMs(end) * scale;
                if (hasNext) endMs = Math.Min(endMs, song.Tempo.TickToMs(next) * scale);
                pl.LenMs = Math.Max(1, endMs - pl.StartMs);
                pl.Opacity = h.Vel / (double)Math.Max(1, part.MaxVel);
                pl.Index = i;

                if (part.Clips)
                {
                    pl.SrcMs = h.SrcMs;
                    double fullMs = song.Tempo.TickToMs(h.End) * scale - pl.StartMs;
                    double tickMs = (song.Tempo.TickToMs(h.End + 1) - song.Tempo.TickToMs(h.End)) * scale;
                    double span = h.SrcEndMs - h.SrcMs;
                    // FL rounds clip lengths to whole ticks, so only a bigger mismatch means the sample is stretched
                    if (h.SrcEndMs > h.SrcMs && fullMs > 0 && Math.Abs(span - fullMs) > 1.5 * tickMs + 0.5) pl.Rate = span / fullMs;
                }
                if (!part.Clips && job.PitchMode != PitchOff)
                {
                    pl.Semis = Math.Max(-24, Math.Min(24, h.Key - job.Root));
                    if (job.PitchMode == PitchResample) pl.Rate *= Math.Pow(2, pl.Semis / 12.0);
                }
                h.Slides.Sort();
                pl.SlideOn = toggled;
                if (job.SlideMode == SlidePop || job.SlideMode == SlideToggle)
                    foreach (long s in h.Slides)
                    {
                        double at = song.Tempo.TickToMs(s) * scale - pl.StartMs;
                        if (at > 0 && at < pl.LenMs - 1) pl.SlideMs.Add(at);
                    }
                if (h.Slides.Count % 2 == 1) toggled = !toggled;   // Toggle: every slide flips the pose on/off, clip to clip
                result.Add(pl);
            }
            return result;
        }

        /// <summary>Merges several channels onto one track: same-time hits keep the loudest, then re-cut.</summary>
        public static List<Placement> Merge(List<List<Placement>> lists)
        {
            List<Placement> all = new List<Placement>();
            foreach (List<Placement> l in lists) all.AddRange(l);
            all.Sort(delegate (Placement a, Placement b)
            {
                int c = a.StartMs.CompareTo(b.StartMs);
                return c != 0 ? c : b.Opacity.CompareTo(a.Opacity);
            });
            List<Placement> result = new List<Placement>();
            foreach (Placement p in all)
                if (result.Count == 0 || Math.Abs(result[result.Count - 1].StartMs - p.StartMs) > 0.01) result.Add(p);
            for (int i = 0; i < result.Count; i++)
            {
                if (i + 1 < result.Count) result[i].LenMs = Math.Min(result[i].LenMs, result[i + 1].StartMs - result[i].StartMs);
                result[i].Index = i;
            }
            return result;
        }
    }

    // =========================================================================================
    //  Effects: model, presets
    // =========================================================================================

    internal enum Fx { Zoom, Rotate, Move, Flip, Fade, Flash, Hue, HueSteps, Saturation, Contrast, Blur, Negative, Greyscale, Reverse, PanCrop }

    /// <summary>
    /// How the picture sits in the frame: Zoom in % (100 = as is), X/Y in % of the frame (+X right, +Y up),
    /// Angle in degrees (+ = clockwise). The editor draws it the Pan/Crop way, as the camera box.
    /// </summary>
    internal struct Pose
    {
        public double Zoom, X, Y, Angle;

        public Pose(double zoom, double x, double y, double angle) { Zoom = zoom; X = x; Y = y; Angle = angle; }

        public static readonly Pose Identity = new Pose(100, 0, 0, 0);

        public bool IsIdentity { get { return Math.Abs(Zoom - 100) < 1e-6 && X == 0 && Y == 0 && Angle == 0; } }

        public static Pose Lerp(Pose a, Pose b, double t)
        {
            return new Pose(a.Zoom + (b.Zoom - a.Zoom) * t, a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Angle + (b.Angle - a.Angle) * t);
        }

        /// <summary>This pose with "extra" laid on top, w of the way (0 = none, 1 = all of it).</summary>
        public Pose Plus(Pose extra, double w)
        {
            return new Pose(Zoom * (1 + (extra.Zoom / 100 - 1) * w), X + extra.X * w, Y + extra.Y * w, Angle + extra.Angle * w);
        }

        public string Serialize()
        {
            return string.Join(",", new string[] { N(Zoom), N(X), N(Y), N(Angle) });
        }

        public static Pose Parse(string s, Pose fallback)
        {
            try
            {
                string[] f = s.Split(',');
                if (f.Length < 4) return fallback;
                return new Pose(Math.Max(1, D(f[0])), D(f[1]), D(f[2]), D(f[3]));
            }
            catch { return fallback; }
        }

        static string N(double v) { return Math.Round(v, 4).ToString("R", CultureInfo.InvariantCulture); }
        static double D(string s) { return double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture); }
    }

    /// <summary>A Pan/Crop keyframe: a pose at T (0 = note start, 1 = note end).</summary>
    internal sealed class PoseKey
    {
        public double T;
        public Pose P;
        public PoseKey(double t, Pose p) { T = t; P = p; }
    }

    internal sealed class EffectSpec
    {
        public Fx Type;
        public double A, B, C, D;         // numbers; meaning depends on Type
        public int Curve = 1;             // 0 linear, 1 ease out, 2 ease in, 3 smooth
        public int Axis;                  // flip: 0 horizontal, 1 vertical, 2 both, 3 clockwise, 4 counterclockwise
        public int Pattern;               // alternating: 0 notes 2,4,6..  1 notes 1,3,5..  2 every note
        public List<PoseKey> Keys = new List<PoseKey>(); // Pan/Crop keyframes, sorted by T

        /// <summary>Pan/Crop pose at t (0..1) along the note.</summary>
        public Pose PoseAt(double t)
        {
            if (Keys.Count == 0) return Pose.Identity;
            if (t <= Keys[0].T) return Keys[0].P;
            for (int i = 1; i < Keys.Count; i++)
                if (t < Keys[i].T)
                {
                    double span = Keys[i].T - Keys[i - 1].T;
                    return span <= 1e-9 ? Keys[i].P : Pose.Lerp(Keys[i - 1].P, Keys[i].P, (t - Keys[i - 1].T) / span);
                }
            return Keys[Keys.Count - 1].P;
        }

        public void SortKeys() { Keys.Sort(delegate (PoseKey a, PoseKey b) { return a.T.CompareTo(b.T); }); }

        public static EffectSpec Create(Fx type)
        {
            EffectSpec e = new EffectSpec();
            e.Type = type;
            switch (type)
            {
                case Fx.PanCrop:
                    e.Keys.Add(new PoseKey(0, new Pose(115, 0, 0, 0)));
                    e.Keys.Add(new PoseKey(1, Pose.Identity));
                    break;
                case Fx.Zoom: e.A = 120; e.B = 100; break;
                case Fx.Rotate: e.A = 15; e.B = 0; break;
                case Fx.Move: e.A = -20; e.B = 0; break;
                case Fx.Fade: e.A = 0; e.B = 40; break;
                case Fx.Flash: e.A = 60; e.B = 40; break;
                case Fx.Hue: e.A = 0; e.B = 360; e.Curve = 0; break;
                case Fx.HueSteps: e.A = 6; break;
                case Fx.Saturation: e.A = 200; e.B = 100; break;
                case Fx.Contrast: e.A = 60; e.B = 0; break;
                case Fx.Blur: e.A = 10; e.B = 0; break;
            }
            return e;
        }

        /// <summary>Does this alternating effect apply to the note with this 0-based index?</summary>
        public bool Hits(int index)
        {
            if (Pattern == 2) return true;
            return Pattern == 1 ? index % 2 == 0 : index % 2 == 1;
        }

        public EffectSpec Clone()
        {
            EffectSpec c = (EffectSpec)MemberwiseClone();
            c.Keys = new List<PoseKey>();
            foreach (PoseKey k in Keys) c.Keys.Add(new PoseKey(k.T, k.P));
            return c;
        }

        public string Serialize()
        {
            List<string> keys = new List<string>();
            foreach (PoseKey k in Keys) keys.Add(Num(k.T) + ":" + k.P.Serialize());
            return string.Join(";", new string[] {
                Type.ToString(), Num(A), Num(B), Num(C), Num(D),
                Curve.ToString(CultureInfo.InvariantCulture), Axis.ToString(CultureInfo.InvariantCulture), Pattern.ToString(CultureInfo.InvariantCulture),
                string.Join("|", keys.ToArray()) });
        }

        public static EffectSpec Parse(string s)
        {
            try
            {
                string[] f = s.Split(';');
                EffectSpec e = Create((Fx)Enum.Parse(typeof(Fx), f[0].Trim(), true));
                if (f.Length > 4) { e.A = Dbl(f[1]); e.B = Dbl(f[2]); e.C = Dbl(f[3]); e.D = Dbl(f[4]); }
                if (f.Length > 7) { e.Curve = Clamp(int.Parse(f[5], CultureInfo.InvariantCulture), 0, 3); e.Axis = Clamp(int.Parse(f[6], CultureInfo.InvariantCulture), 0, 4); e.Pattern = Clamp(int.Parse(f[7], CultureInfo.InvariantCulture), 0, 2); }
                if (f.Length > 8 && f[8].Length > 0)
                {
                    e.Keys.Clear();
                    foreach (string k in f[8].Split('|'))
                    {
                        int c = k.IndexOf(':');
                        if (c > 0) e.Keys.Add(new PoseKey(Math.Max(0, Math.Min(1, Dbl(k.Substring(0, c)))), Pose.Parse(k.Substring(c + 1), Pose.Identity)));
                    }
                    e.SortKeys();
                }
                return e;
            }
            catch { return null; }
        }

        static string Num(double v) { return v.ToString("R", CultureInfo.InvariantCulture); }
        static double Dbl(string s) { return double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture); }
        static int Clamp(int v, int lo, int hi) { return v < lo ? lo : v > hi ? hi : v; }
    }

    internal static class FxInfo
    {
        public static readonly string[] GroupNames = { "MOTION", "COLOR", "LIGHT && TIME" }; // "&&" shows a single "&" in menus
        public static readonly Fx[][] Groups = {
            new Fx[] { Fx.PanCrop, Fx.Zoom, Fx.Rotate, Fx.Move, Fx.Flip },
            new Fx[] { Fx.Hue, Fx.HueSteps, Fx.Saturation, Fx.Contrast, Fx.Negative, Fx.Greyscale },
            new Fx[] { Fx.Flash, Fx.Fade, Fx.Blur, Fx.Reverse } };

        public static string Name(Fx t)
        {
            switch (t)
            {
                case Fx.Hue: return "Hue shift";
                case Fx.HueSteps: return "Hue steps";
                case Fx.PanCrop: return "Pan/Crop";
                default: return t.ToString();
            }
        }

        /// <summary>Short description shown beside the name in the "+ Add effect" menu.</summary>
        public static string Short(Fx t)
        {
            switch (t)
            {
                case Fx.PanCrop: return "draw keyframes, like VEGAS Pan/Crop";
                case Fx.Zoom: return "scale in or out over each note";
                case Fx.Rotate: return "turn over each note";
                case Fx.Move: return "slide over each note";
                case Fx.Flip: return "mirror per note, or cycle round";
                case Fx.Fade: return "fade each clip in / out";
                case Fx.Flash: return "bright hit that fades away";
                case Fx.Hue: return "color sweep over each note";
                case Fx.HueSteps: return "new color on every note";
                case Fx.Saturation: return "color punch over each note";
                case Fx.Contrast: return "contrast punch over each note";
                case Fx.Blur: return "blur that clears over each note";
                case Fx.Negative: return "invert colors on a note pattern";
                case Fx.Greyscale: return "black and white on a note pattern";
                case Fx.Reverse: return "play backwards on a note pattern";
            }
            return "";
        }

        public static string Hint(Fx t)
        {
            switch (t)
            {
                case Fx.PanCrop: return "Keyframed camera box over each note, set by dragging like VEGAS Pan/Crop. Stacks with Zoom / Rotate / Move.";
                case Fx.Zoom: return "Scales each clip from one size to another over its note (100% = normal).";
                case Fx.Rotate: return "Rotates each clip over its note. Positive = clockwise.";
                case Fx.Move: return "Slides each clip, in % of the frame. X: + is right. Y: + is up.";
                case Fx.Flip: return "Horizontal / Vertical / Both mirror on a note pattern. Clockwise / Counterclockwise cycle through 4 flips, one step per note (like Otomad Helper).";
                case Fx.Fade: return "Fades each clip in/out, as % of its length.";
                case Fx.Flash: return "Brightness flash at the start of each note that decays away.";
                case Fx.Hue: return "Rotates the hue over each note (VEGAS HSL Adjust).";
                case Fx.HueSteps: return "Each new note jumps further round the color wheel.";
                case Fx.Saturation: return "Animates saturation over each note (100% = normal).";
                case Fx.Contrast: return "Animates contrast over each note (0 = normal).";
                case Fx.Blur: return "Gaussian blur that animates over each note.";
                case Fx.Negative: return "Inverts colors on a note pattern.";
                case Fx.Greyscale: return "Black & white on a note pattern.";
                case Fx.Reverse: return "Plays clips backwards on a note pattern (ping-pong).";
            }
            return "";
        }
    }

    internal sealed class Settings
    {
        public string LastFlp = "", Arrangement = "";
        public int StartMode, Destination, MinLen, MaxLen, LongMode, StepLen, SlideMode, PitchMode, Root = 60, Tab;
        public Pose SlidePose = new Pose(112, 0, 0, 0);
        public double SlideEaseMs = 150;
        public string CustomStart = "0:00.000";
        public bool Velocity = true, IncludeMuted, Audio = true;
        public string Preset = "";
        public int WinW, WinH;            // 96-dpi units
        public readonly List<string> Checked = new List<string>();
        public readonly List<EffectSpec> Effects = new List<EffectSpec>();

        static string Dir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FLP to Video"); } }
        static string FilePath { get { return Path.Combine(Dir, "settings.txt"); } }
        static string PresetDir { get { return Path.Combine(Dir, "Presets"); } }
        const string PresetExt = ".preset";

        public static Settings Load()
        {
            Settings s = new Settings();
            try
            {
                if (!File.Exists(FilePath)) return s;
                foreach (string line in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim(), v = line.Substring(eq + 1);
                    switch (k)
                    {
                        case "flp": s.LastFlp = v; break;
                        case "arrangement": s.Arrangement = v; break;
                        case "stepLength": s.StepLen = Int(v, 0, Planner.StepNames.Length - 1); break;
                        case "slides": s.SlideMode = Int(v, 0, Planner.SlideNames.Length - 1); break;
                        case "pitch": s.PitchMode = Int(v, 0, Planner.PitchNames.Length - 1); break;
                        case "slidePose": s.SlidePose = Pose.Parse(v, s.SlidePose); break;
                        case "slideEase": s.SlideEaseMs = Int(v, 0, 5000); break;
                        case "tab": s.Tab = Int(v, 0, 3); break;
                        case "root": s.Root = Int(v, Planner.RootLow, Planner.RootHigh); break;
                        case "muted": s.IncludeMuted = v.Trim() == "1"; break;
                        case "audio": s.Audio = v.Trim() != "0"; break;
                        case "start": s.StartMode = Int(v, 0, 2); break;
                        case "customStart": s.CustomStart = v; break;
                        case "destination": s.Destination = Int(v, 0, 1); break;
                        case "minLength": s.MinLen = Int(v, 0, Planner.LengthNames.Length - 1); break;
                        case "maxLength": s.MaxLen = Int(v, 0, Planner.LengthNames.Length - 1); break;
                        case "longNotes": s.LongMode = Int(v, 0, 2); break;
                        case "velocity": s.Velocity = v.Trim() != "0"; break;
                        case "preset": s.Preset = v; break;
                        case "size": { string[] wh = v.Split('x'); if (wh.Length == 2) { s.WinW = Int(wh[0], 0, 10000); s.WinH = Int(wh[1], 0, 10000); } break; }
                        case "checked": s.Checked.Add(v); break;
                        case "fx": { EffectSpec e = EffectSpec.Parse(v); if (e != null) s.Effects.Add(e); break; }
                    }
                }
            }
            catch { }
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                List<string> l = new List<string>();
                l.Add("flp=" + LastFlp);
                l.Add("arrangement=" + Arrangement);
                l.Add("stepLength=" + StepLen);
                l.Add("slides=" + SlideMode);
                l.Add("pitch=" + PitchMode);
                l.Add("slidePose=" + SlidePose.Serialize());
                l.Add("slideEase=" + (int)Math.Round(SlideEaseMs));
                l.Add("tab=" + Tab);
                l.Add("root=" + Root);
                l.Add("muted=" + (IncludeMuted ? "1" : "0"));
                l.Add("audio=" + (Audio ? "1" : "0"));
                l.Add("start=" + StartMode);
                l.Add("customStart=" + CustomStart);
                l.Add("destination=" + Destination);
                l.Add("minLength=" + MinLen);
                l.Add("maxLength=" + MaxLen);
                l.Add("longNotes=" + LongMode);
                l.Add("velocity=" + (Velocity ? "1" : "0"));
                l.Add("preset=" + Preset);
                if (WinW > 0 && WinH > 0) l.Add("size=" + WinW + "x" + WinH);
                foreach (string c in Checked) l.Add("checked=" + c);
                foreach (EffectSpec e in Effects) l.Add("fx=" + e.Serialize());
                File.WriteAllLines(FilePath, l.ToArray(), Encoding.UTF8);
            }
            catch { }
        }

        static int Int(string v, int lo, int hi)
        {
            int x;
            if (!int.TryParse(v.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out x)) return lo;
            return x < lo ? lo : x > hi ? hi : x;
        }

        // ---- presets: one small text file per preset ----

        public static List<string> PresetNames()
        {
            List<string> names = new List<string>();
            try
            {
                if (Directory.Exists(PresetDir))
                    foreach (string f in Directory.GetFiles(PresetDir, "*" + PresetExt))
                        names.Add(Path.GetFileNameWithoutExtension(f));
            }
            catch { }
            names.Sort(StringComparer.CurrentCultureIgnoreCase);
            return names;
        }

        public static string CleanName(string name)
        {
            if (name == null) return "";
            StringBuilder b = new StringBuilder();
            foreach (char c in name.Trim())
                if (Array.IndexOf(Path.GetInvalidFileNameChars(), c) < 0 && "\\/:*?\"<>|".IndexOf(c) < 0) b.Append(c);
            return b.ToString().Trim().TrimEnd('.');
        }

        static string PresetPath(string name) { return Path.Combine(PresetDir, CleanName(name) + PresetExt); }

        public static bool PresetExists(string name) { return File.Exists(PresetPath(name)); }

        public static List<EffectSpec> LoadPreset(string name)
        {
            List<EffectSpec> list = new List<EffectSpec>();
            foreach (string line in File.ReadAllLines(PresetPath(name), Encoding.UTF8))
            {
                if (!line.StartsWith("fx=")) continue;
                EffectSpec e = EffectSpec.Parse(line.Substring(3));
                if (e != null) list.Add(e);
            }
            return list;
        }

        public static void SavePreset(string name, List<EffectSpec> effects)
        {
            Directory.CreateDirectory(PresetDir);
            List<string> l = new List<string>();
            foreach (EffectSpec e in effects) l.Add("fx=" + e.Serialize());
            File.WriteAllLines(PresetPath(name), l.ToArray(), Encoding.UTF8);
        }

        public static void DeletePreset(string name)
        {
            string p = PresetPath(name);
            if (File.Exists(p)) File.Delete(p);
        }
    }

    // =========================================================================================
    //  Generation (all VEGAS edits happen here, inside one undo step)
    // =========================================================================================

    internal sealed class Plugins
    {
        public PlugInNode Hsl, BrightContrast, Blur, Invert, BlackWhite;

        public static Plugins Resolve(Vegas vegas, Job job)
        {
            Plugins p = new Plugins();
            List<string> missing = new List<string>();
            if (job.Get(Fx.Hue) != null || job.Get(Fx.HueSteps) != null || job.Get(Fx.Saturation) != null)
                p.Hsl = Need(vegas, "hsladjust", "HSL Adjust", missing);
            if (job.Get(Fx.Contrast) != null || job.Get(Fx.Flash) != null)
                p.BrightContrast = Need(vegas, "brightnessandcontrast", "Brightness and Contrast", missing);
            if (job.Get(Fx.Blur) != null) p.Blur = Need(vegas, "gaussianblur", "Gaussian Blur", missing);
            if (job.Get(Fx.Negative) != null) p.Invert = Need(vegas, "invert", "Invert", missing);
            if (job.Get(Fx.Greyscale) != null) p.BlackWhite = Need(vegas, "blackandwhite", "Black and White", missing);
            if (missing.Count > 0)
                throw new UserError("These built-in VEGAS video effects couldn't be found:\n\n  " +
                    string.Join("\n  ", missing.ToArray()) + "\n\nRemove the effects that use them and try again.");
            return p;
        }

        static PlugInNode Need(Vegas vegas, string id, string name, List<string> missing)
        {
            PlugInNode n = Find(vegas.VideoFX, id, name);
            if (n == null) missing.Add("VEGAS " + name);
            return n;
        }

        static PlugInNode Find(PlugInNode root, string id, string name)
        {
            if (root == null) return null;
            string[] ids = { "{Svfx:com.vegascreativesoftware:" + id + "}", "{Svfx:com.sonycreativesoftware:" + id + "}" };
            foreach (string uid in ids)
            {
                try { PlugInNode n = root.FindChildByUniqueID(uid); if (n != null) return n; } catch { }
            }
            string[] names = { "VEGAS " + name, name, "Sony " + name };
            foreach (string nm in names)
            {
                try { PlugInNode n = root.FindChildByName(nm); if (n != null) return n; } catch { }
            }
            return null;
        }
    }

    internal static class Generator
    {
        public const int Extend = 0, Freeze = 1, Stretch = 2;

        public static void Run(Vegas vegas, Job job)
        {
            if (job.Parts.Count == 0) return;
            if (!job.NewTracks && job.Target == null) throw new UserError("Select a video track to place the clips on.");
            foreach (Part part in job.Parts)
                if (job.SourceFor(part) == null)
                    throw new UserError("“" + part.Name + "” has no clip.\n\nSelect a clip on the timeline, or name a Project Media subclip after it.");
            Plugins fx = Plugins.Resolve(vegas, job);

            // Plan everything first so nothing is touched if the plan is empty.
            List<List<Placement>> plans = new List<List<Placement>>();
            List<string> names = new List<string>();
            List<List<Placement>> each = new List<List<Placement>>();
            foreach (Part part in job.Parts)
            {
                List<Placement> l = Planner.Build(job.Song, part, job);
                foreach (Placement p in l) p.Source = job.SourceFor(part);
                each.Add(l);
            }
            if (job.NewTracks)
            {
                plans.AddRange(each);
                foreach (Part part in job.Parts) names.Add(part.Label);
            }
            else
            {
                plans.Add(Planner.Merge(each));
                names.Add(null);
            }
            int total = 0;
            foreach (List<Placement> l in plans) total += l.Count;
            if (total == 0) throw new UserError("The selected channels have no notes" + (job.IncludeMuted ? "." : " that aren't muted."));
            bool audio = false;
            foreach (Part part in job.Parts) audio |= job.Audio && job.SourceFor(part).HasAudio;
            SourceClip anchor = job.Default;
            if (anchor == null || anchor.Track == null)
                foreach (Part part in job.Parts) if (job.SourceFor(part).Track != null) { anchor = job.SourceFor(part); break; }

            IntPtr main = Native.VegasHandle();
            ProgressWindow progress = new ProgressWindow(total);
            try
            {
                Native.Enable(main, false);       // keep the timeline still while we work
                progress.Show(Native.VegasWindow());
                progress.Refresh();
                using (UndoBlock undo = new UndoBlock("FLP to Video"))
                {
                    int insertAt = anchor != null && anchor.Track != null ? anchor.Track.Index : 0; // new tracks go just above the clip
                    for (int i = 0; i < plans.Count && !progress.Cancelled; i++)
                    {
                        VideoTrack track = job.Target;
                        if (job.NewTracks)
                        {
                            track = new VideoTrack(vegas.Project, insertAt++, names[i]);
                            vegas.Project.Tracks.Add(track);
                        }
                        AudioTrack audioTrack = null;
                        if (audio)
                        {
                            // its own audio track right below the video track
                            int at = job.NewTracks ? insertAt++ : track.Index + 1;
                            audioTrack = new AudioTrack(vegas.Project, at, job.NewTracks ? names[i] : track.Name);
                            vegas.Project.Tracks.Add(audioTrack);
                        }
                        foreach (Placement p in plans[i])
                        {
                            VideoEvent ev = Place(track, p.Source, p, job, fx);
                            if (audioTrack != null && p.Source.HasAudio)
                            {
                                AudioEvent ae = p.Source.CreateAudio(audioTrack, ev);
                                Pitch(ae, p, job);
                                TrackEventGroup g = new TrackEventGroup(vegas.Project);
                                vegas.Project.Groups.Add(g);
                                g.Add(ev);
                                g.Add(ae);
                            }
                            if (progress.Step()) break;
                        }
                    }
                }
            }
            finally
            {
                Native.Enable(main, true);
                progress.Close();
                progress.Dispose();
            }
        }

        static Timecode Ms(double ms) { return Timecode.FromMilliseconds(ms); }

        static VideoEvent Place(VideoTrack track, SourceClip src, Placement pl, Job job, Plugins plugins)
        {
            double len = pl.LenMs;
            VideoEvent ev = src.Create(track, Ms(job.StartMs + pl.StartMs), Ms(len));
            ev.Selected = false;
            ev.FadeIn.Length = Ms(0);
            ev.FadeOut.Length = Ms(0);

            // ---- audio clip: start where the FL clip starts inside the sample, at the clip's speed ----
            bool chopped = pl.SrcMs > 0 || Math.Abs(pl.Rate - 1) > 1e-9;
            if (Math.Abs(pl.Rate - 1) > 1e-9) SetRate(ev, src.Rate * pl.Rate, Ms(len));
            if (pl.SrcMs > 0 && ev.ActiveTake != null) ev.ActiveTake.Offset = ev.ActiveTake.Offset + Ms(pl.SrcMs * src.Rate);

            // ---- note longer than the clip ----
            double rate = ev.PlaybackRate;
            double left = (src.LengthMs - pl.SrcMs) / pl.Rate;  // timeline ms until the source runs out
            double freezeAt = -1;                 // event time where the clip's last frame is reached
            if (len > left + 0.5)
            {
                if (job.LongMode == Freeze || (chopped && job.LongMode == Stretch)) freezeAt = Math.Max(1, left);
                else if (job.LongMode == Stretch)
                {
                    SetRate(ev, src.MediaSpanMs / len, Ms(len));
                    rate = ev.PlaybackRate;       // VEGAS clamps (usually to 0.25x); hold the last frame after that
                    double reach = src.MediaSpanMs / rate;
                    if (reach < len - 0.5) freezeAt = reach;
                }
            }

            EffectSpec reverse = job.Get(Fx.Reverse);
            if (reverse != null && reverse.Hits(pl.Index)) Speed.Reverse(ev, freezeAt >= 0 ? freezeAt : len, rate, freezeAt);
            else if (freezeAt >= 0) Speed.Freeze(ev, freezeAt);

            // ---- velocity -> opacity (loudest note of the channel = 100%) ----
            double opacity = job.Velocity ? pl.Opacity : 1.0;
            ev.FadeIn.Gain = (float)Math.Max(0.0, Math.Min(1.0, src.Gain * opacity));

            EffectSpec fade = job.Get(Fx.Fade);
            if (fade != null)
            {
                double a = Clamp(fade.A, 0, 100), b = Clamp(fade.B, 0, 100);
                if (a + b > 100) { double k = 100 / (a + b); a *= k; b *= k; }
                ev.FadeIn.Length = Ms(len * a / 100);
                ev.FadeOut.Length = Ms(len * b / 100);
            }

            Motion.Apply(ev, len, job, pl);
            Looks.Apply(ev, len, job, pl.Index, plugins);
            return ev;
        }

        public static double Clamp(double v, double lo, double hi) { return v < lo ? lo : v > hi ? hi : v; }

        /// <summary>
        /// Shift: élastique pitch shift, length untouched. Resample: the speed change already made the
        /// note's pitch, so "lock to stretch" lets the audio's pitch follow its rate like a tape.
        /// </summary>
        static void Pitch(AudioEvent ae, Placement p, Job job)
        {
            try
            {
                if (p.Semis == 0 || job.PitchMode == Planner.PitchOff) return;
                ae.Method = TimeStretchPitchShift.Elastique;
                ae.ElastiqueAttribute = ElastiqueStretchAttributes.Pro;
                if (job.PitchMode == Planner.PitchShift)
                {
                    ae.PitchLock = false;
                    ae.PitchSemis = p.Semis;
                }
                else ae.PitchLock = true;
            }
            catch { } // pitch is a nicety; never stop the run over it
        }

        /// <summary>
        /// Changes an event's speed and keeps it starting on the same source frame. AdjustPlaybackRate
        /// rescales the take offset (a subclip 24 s into its file played from 71 s at 0.34x), so put it back.
        /// </summary>
        public static void SetRate(TrackEvent ev, double rate, Timecode length)
        {
            List<Timecode> offsets = new List<Timecode>();
            foreach (Take t in ev.Takes) offsets.Add(t.Offset);
            ev.AdjustPlaybackRate(rate, true);
            ev.Length = length;
            for (int i = 0; i < offsets.Count && i < ev.Takes.Count; i++) ev.Takes[i].Offset = offsets[i];
        }
    }

    /// <summary>Velocity-envelope tricks: hold the last frame, play backwards.</summary>
    internal static class Speed
    {
        static Envelope Velocity(VideoEvent ev)
        {
            Envelope env = ev.Envelopes.FindByType(EnvelopeType.Velocity);
            if (env == null)
            {
                env = new Envelope(EnvelopeType.Velocity);
                ev.Envelopes.Add(env);
            }
            env.Points.Clear();
            return env;
        }

        static void Point(Envelope env, double ms, double y)
        {
            Timecode t = Timecode.FromMilliseconds(ms);
            EnvelopePoint p = env.Points.GetPointAtX(t);
            if (p == null) env.Points.Add(new EnvelopePoint(t, y, CurveType.None));
            else { p.Y = y; p.Curve = CurveType.None; }
        }

        public static void Freeze(VideoEvent ev, double atMs)
        {
            Envelope env = Velocity(ev);
            env.Points[0].Y = 1;
            env.Points[0].Curve = CurveType.None;
            Point(env, atMs, 0);
        }

        /// <summary>Starts at the last frame that would have played and runs backwards to the clip start.</summary>
        public static void Reverse(VideoEvent ev, double playedMs, double rate, double freezeAt)
        {
            Take take = ev.ActiveTake;
            if (take == null) return;
            take.Offset = take.Offset + Timecode.FromMilliseconds(playedMs * rate);
            Envelope env = Velocity(ev);
            env.Points[0].Y = -1;
            env.Points[0].Curve = CurveType.None;
            if (freezeAt > 0) Point(env, freezeAt, 0);
        }
    }

    /// <summary>Zoom / rotate / move / flip through event Pan/Crop keyframes.</summary>
    internal static class Motion
    {
        /// <summary>
        /// Builds the clip's Pan/Crop keyframes from three layers: the Zoom / Rotate / Move effects (start -> end),
        /// the Pan/Crop effect's own keyframes, and the slide pose (Pop: jump in and settle back; Toggle: switch
        /// on/off at every slide). Each keyframe gets the exact combined pose; VEGAS interpolates between them.
        /// </summary>
        public static void Apply(VideoEvent ev, double len, Job job, Placement pl)
        {
            EffectSpec zoom = job.Get(Fx.Zoom), rot = job.Get(Fx.Rotate), move = job.Get(Fx.Move), flip = job.Get(Fx.Flip), pan = job.Get(Fx.PanCrop);
            bool flipH, flipV;
            FlipState(flip, pl.Index, out flipH, out flipV);
            List<double> slides = pl.SlideMs;
            bool pop = job.SlideMode == Planner.SlidePop && slides.Count > 0;
            bool toggle = job.SlideMode == Planner.SlideToggle && (slides.Count > 0 || pl.SlideOn);
            if (job.SlidePose.IsIdentity) pop = toggle = false;
            if (zoom == null && rot == null && move == null && pan == null && !flipH && !flipV && !pop && !toggle) return;

            double z0 = zoom != null ? zoom.A : 100, z1 = zoom != null ? zoom.B : 100;
            double r0 = rot != null ? rot.A : 0, r1 = rot != null ? rot.B : 0;
            double x0 = 0, x1 = 0, y0 = 0, y1 = 0;
            if (move != null) { x0 = move.A; x1 = move.B; y0 = move.C; y1 = move.D; }
            bool animated = z0 != z1 || r0 != r1 || x0 != x1 || y0 != y1;
            int curve = 0;
            if (zoom != null && z0 != z1) curve = zoom.Curve;
            else if (rot != null && r0 != r1) curve = rot.Curve;
            else if (move != null) curve = move.Curve;

            // keyframe times (ms into the clip) and the type of the segment each one starts
            SortedDictionary<double, VideoKeyframeType> times = new SortedDictionary<double, VideoKeyframeType>();
            times[0] = KeyType(curve);
            if (animated || pan != null || pop || toggle) Mark(times, len, VideoKeyframeType.Linear);
            if (pan != null)
                foreach (PoseKey k in pan.Keys) Mark(times, k.T * len, KeyType(pan.Curve));
            double ease = Math.Max(1, job.SlideEaseMs);
            for (int i = 0; i < slides.Count && (pop || toggle); i++)
            {
                double s = slides[i], until = i + 1 < slides.Count ? slides[i + 1] : len;
                double e = Math.Max(1, Math.Min(ease, (until - s) * 0.8));
                if (pop)
                {
                    if (s - 1 > 0) Mark(times, s - 1, VideoKeyframeType.Linear);   // 1 ms jump into the pose...
                    Mark(times, s, VideoKeyframeType.Fast);                          // ...then ease back out of it
                }
                else Mark(times, s, VideoKeyframeType.Smooth);                     // glide to the other state
                Mark(times, Math.Min(len, s + e), VideoKeyframeType.Linear);
            }

            VideoMotionKeyframes keys = ev.VideoMotion.Keyframes;
            // Added before transforming, so they start from the clip's own framing.
            foreach (double at in times.Keys) AddKey(keys, at);
            foreach (VideoMotionKeyframe k in keys)
            {
                double at = k.Position.ToMilliseconds();
                double t = len > 0 ? Generator.Clamp(at / len, 0, 1) : 0;
                Pose p = new Pose(Lerp(z0, z1, t), Lerp(x0, x1, t), Lerp(y0, y1, t), Lerp(r0, r1, t));
                if (pan != null) p = p.Plus(pan.PoseAt(t), 1);
                if (pop || toggle) p = p.Plus(job.SlidePose, SlideWeight(at, slides, pop, pl.SlideOn, ease, len));
                Transform(k, flipH, flipV, p.Zoom, p.Angle, p.X, p.Y);
                VideoKeyframeType type;
                if (times.TryGetValue(Snap(times, at), out type)) k.Type = type;
            }
        }

        /// <summary>0..1: how much of the slide pose shows at this moment.</summary>
        static double SlideWeight(double at, List<double> slides, bool pop, bool startOn, double ease, double len)
        {
            if (pop)
            {
                double w = 0;
                for (int i = 0; i < slides.Count; i++)
                {
                    double s = slides[i], until = i + 1 < slides.Count ? slides[i + 1] : len;
                    double e = Math.Max(1, Math.Min(ease, (until - s) * 0.8));
                    if (at >= s - 0.01 && at < s + e) w = Math.Max(w, 1 - (at - s) / e);
                }
                return Math.Max(0, Math.Min(1, w));
            }
            double state = startOn ? 1 : 0;
            for (int i = 0; i < slides.Count; i++)
            {
                double s = slides[i], until = i + 1 < slides.Count ? slides[i + 1] : len;
                double e = Math.Max(1, Math.Min(ease, (until - s) * 0.8));
                if (at >= s + e - 0.01) { state = 1 - state; continue; }
                if (at > s) return state + ((1 - state) - state) * (at - s) / e;
                break;
            }
            return state;
        }

        static void Mark(SortedDictionary<double, VideoKeyframeType> times, double ms, VideoKeyframeType type)
        {
            double at = Snap(times, ms);
            times[at] = type;
        }

        static double Snap(SortedDictionary<double, VideoKeyframeType> times, double ms)
        {
            foreach (double t in times.Keys) if (Math.Abs(t - ms) < 0.5) return t;
            return ms;
        }

        static void AddKey(VideoMotionKeyframes keys, double ms)
        {
            foreach (VideoMotionKeyframe k in keys)
                if (Math.Abs(k.Position.ToMilliseconds() - ms) < 0.5) return;
            keys.Add(new VideoMotionKeyframe(Timecode.FromMilliseconds(ms)));
        }

        static double Lerp(double a, double b, double t) { return a + (b - a) * t; }

        /// <summary>
        /// Horizontal / Vertical / Both follow the note pattern. Clockwise and Counterclockwise step through
        /// four states, one per note, exactly like Otomad Helper's CW / CCW flip:
        ///   Clockwise:        normal, vertical, both, horizontal, ...
        ///   Counterclockwise: normal, horizontal, both, vertical, ...
        /// </summary>
        public static void FlipState(EffectSpec flip, int index, out bool h, out bool v)
        {
            h = v = false;
            if (flip == null) return;
            if (flip.Axis >= 3)
            {
                int step = index % 4;
                bool cw = flip.Axis == 3;
                h = cw ? (step == 2 || step == 3) : (step == 1 || step == 2);
                v = cw ? (step == 1 || step == 2) : (step == 2 || step == 3);
                return;
            }
            if (!flip.Hits(index)) return;
            h = flip.Axis != 1;
            v = flip.Axis != 0;
        }

        /// <summary>
        /// Pan/Crop moves the "camera" box, so everything is inverted: a smaller box = bigger picture.
        /// Directions are measured along the box's own axes so they stay right when flipped.
        /// </summary>
        static void Transform(VideoMotionKeyframe k, bool flipH, bool flipV, double zoomPct, double degrees, double movePctX, double movePctY)
        {
            if (flipH || flipV) k.ScaleBy(new VideoMotionVertex(flipH ? -1f : 1f, flipV ? -1f : 1f));
            double zoom = Math.Max(0.01, zoomPct / 100.0);
            if (Math.Abs(zoom - 1) > 1e-6)
            {
                float s = (float)(1 / zoom);
                k.ScaleBy(new VideoMotionVertex(s, s));
            }
            double ux = k.TopRight.X - k.TopLeft.X, uy = k.TopRight.Y - k.TopLeft.Y;     // box width axis
            double vx = k.BottomLeft.X - k.TopLeft.X, vy = k.BottomLeft.Y - k.TopLeft.Y; // box height axis
            if (movePctX != 0 || movePctY != 0)
            {
                double mx = movePctX / 100.0, my = movePctY / 100.0;
                k.MoveBy(new VideoMotionVertex((float)(-mx * ux + my * vx), (float)(-mx * uy + my * vy)));
            }
            if (degrees != 0)
            {
                double mirrored = (ux * vy - uy * vx) < 0 ? -1 : 1;     // a mirrored box turns the other way
                k.RotateBy(-degrees * Math.PI / 180.0 * mirrored);       // RotateBy(+) turns the picture counter-clockwise
            }
        }

        static VideoKeyframeType KeyType(int curve)
        {
            switch (curve)
            {
                case 1: return VideoKeyframeType.Fast;
                case 2: return VideoKeyframeType.Slow;
                case 3: return VideoKeyframeType.Smooth;
                default: return VideoKeyframeType.Linear;
            }
        }
    }

    /// <summary>Color / light effects through VEGAS's built-in OFX plug-ins.</summary>
    internal static class Looks
    {
        public static void Apply(VideoEvent ev, double len, Job job, int index, Plugins p)
        {
            EffectSpec neg = job.Get(Fx.Negative);
            if (neg != null && neg.Hits(index)) ev.Effects.AddEffect(p.Invert);
            EffectSpec bw = job.Get(Fx.Greyscale);
            if (bw != null && bw.Hits(index)) ev.Effects.AddEffect(p.BlackWhite);

            EffectSpec hue = job.Get(Fx.Hue), sat = job.Get(Fx.Saturation);
            bool satUsed = sat != null && !(sat.A == 100 && sat.B == 100);
            if (hue != null || satUsed)
            {
                Effect hsl = ev.Effects.AddEffect(p.Hsl);
                if (hue != null) Animate(Param(hsl, "AddToHue"), Generator.Clamp(hue.A, 0, 360) / 360, Generator.Clamp(hue.B, 0, 360) / 360, len, hue.Curve);
                if (satUsed) Animate(Param(hsl, "Saturation"), sat.A / 100, sat.B / 100, len, sat.Curve);
            }
            EffectSpec steps = job.Get(Fx.HueSteps);
            if (steps != null)
            {
                int n = (int)Math.Max(2, Math.Round(steps.A));
                if (index % n != 0) Param(ev.Effects.AddEffect(p.Hsl), "AddToHue").Value = (index % n) / (double)n;
            }

            EffectSpec con = job.Get(Fx.Contrast), flash = job.Get(Fx.Flash);
            if (con != null || (flash != null && flash.A > 0))
            {
                Effect bc = ev.Effects.AddEffect(p.BrightContrast);
                if (con != null) Animate(Param(bc, "Contrast"), con.A / 100, con.B / 100, len, con.Curve);
                if (flash != null && flash.A > 0)
                {
                    OFXDoubleParameter bright = Param(bc, "Brightness");
                    bright.IsAnimated = true;
                    bright.SetValueAtTime(Timecode.FromMilliseconds(0), Generator.Clamp(flash.A, 0, 100) / 100);
                    bright.SetValueAtTime(Timecode.FromMilliseconds(len * Generator.Clamp(flash.B, 1, 100) / 100), 0);
                    bright.Keyframes[0].Interpolation = OFXInterpolationType.Fast;
                }
            }

            EffectSpec blur = job.Get(Fx.Blur);
            if (blur != null)
            {
                Effect gb = ev.Effects.AddEffect(p.Blur);
                Animate(Param(gb, "HorizontalRange"), blur.A / 100, blur.B / 100, len, blur.Curve);
                Animate(Param(gb, "VerticalRange"), blur.A / 100, blur.B / 100, len, blur.Curve);
            }
        }

        static OFXDoubleParameter Param(Effect fx, string name)
        {
            OFXDoubleParameter p = fx.OFXEffect.FindParameterByName(name) as OFXDoubleParameter;
            if (p == null) throw new UserError("A VEGAS effect is missing its \"" + name + "\" setting, so the run was stopped.");
            return p;
        }

        static void Animate(OFXDoubleParameter p, double a, double b, double len, int curve)
        {
            if (Math.Abs(a - b) < 1e-9) { p.Value = a; return; }
            p.IsAnimated = true;
            p.SetValueAtTime(Timecode.FromMilliseconds(0), a);
            p.SetValueAtTime(Timecode.FromMilliseconds(len), b);
            p.Keyframes[0].Interpolation = Curve(curve);
        }

        static OFXInterpolationType Curve(int curve)
        {
            switch (curve)
            {
                case 1: return OFXInterpolationType.Fast;
                case 2: return OFXInterpolationType.Slow;
                case 3: return OFXInterpolationType.Smooth;
                default: return OFXInterpolationType.Linear;
            }
        }
    }

    // =========================================================================================
    //  Main window
    // =========================================================================================

    internal sealed class MainForm : Form
    {
        // Layout in 96-dpi pixels (scaled by Theme.D).
        const int M = 16, HEAD = 58, FOOT = 58, GAP = 10, LEFT = 260, PAD = 16, KEY = 92, ROW = 28, PITCH = 38, TOP = 34;
        public static readonly string[] TabNames = { "Placement", "Timing", "Notes", "Effects" };

        static readonly string[] LongHints = {
            "Extend: the clip keeps playing past its end.",
            "Freeze: the clip holds its last frame.",
            "Stretch: the clip slows down to fill the note." };

        readonly SourceClip source;       // selected on the timeline; may be null when subclips cover everything
        readonly Subclips subclips;
        readonly Settings settings;
        readonly double cursorMs;
        readonly VideoTrack targetTrack;
        readonly string targetName;
        FlpProject flp;
        Song song;
        double bpmShown;
        bool[] picked = new bool[0];
        readonly Dictionary<Part, SourceClip> matched = new Dictionary<Part, SourceClip>();
        readonly List<EffectSpec> effects = new List<EffectSpec>();
        readonly List<EffectRow> rows = new List<EffectRow>();
        readonly ToolTip tips;

        Label titleLbl, fileLbl, bpmUnit, clipKey, clipVal, startKey, destKey, audioKey, lenKey, toLbl,
            stepKey, slideKey, easeKey, easeUnit, pitchKey, rootKey, longKey, opKey, mutedKey, mutedHint,
            poseKey, poseHint, notesEmpty, fxEmpty, summaryLbl;
        NumBox bpmBox, easeBox;
        TimeField customBox;
        Btn changeBtn, allBtn, noneBtn, addFxBtn, savePresetBtn, delPresetBtn, cancelBtn, goBtn;
        Card chCard, tabCard;
        TabStrip tabs;
        Panel[] pages;
        ChannelList chList;
        Segmented startSeg, destSeg, longSeg, slideSeg, pitchSeg, mutedSeg;
        Dropdown arrDd, minDd, maxDd, stepDd, rootDd, presetDd;
        Toggle opToggle, audioToggle;
        PosePanel slidePose;
        Panel fxPanel;

        static int D(double v) { return Theme.D(v); }

        public MainForm(Vegas vegas, SourceClip source, Subclips subclips, FlpProject flp, Settings st, bool restoreChecks)
            : this(vegas.Transport.CursorPosition.ToMilliseconds(), SelectedTrack(vegas, source), source, subclips, flp, st, restoreChecks) { }

        static VideoTrack SelectedTrack(Vegas vegas, SourceClip source)
        {
            foreach (Track t in vegas.Project.Tracks)
                if (t.Selected && t.IsVideo()) return t as VideoTrack;
            return source != null ? source.Track as VideoTrack : null;
        }

        internal MainForm(double cursorMs, VideoTrack targetTrack, SourceClip source, Subclips subclips, FlpProject flp, Settings st, bool restoreChecks)
        {
            this.source = source;
            this.subclips = subclips;
            this.settings = st;
            this.cursorMs = cursorMs;
            this.targetTrack = targetTrack;
            if (targetTrack != null)
                targetName = string.IsNullOrEmpty(targetTrack.Name) ? "Track " + (targetTrack.Index + 1) : targetTrack.Name;

            Text = "FLP to Video";
            AutoScaleMode = AutoScaleMode.None;
            Font = Theme.Base;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ShowIcon = false;
            StartPosition = FormStartPosition.CenterParent;
            DoubleBuffered = true;
            KeyPreview = true;
            tips = Theme.MakeToolTip();

            Build();
            foreach (EffectSpec e in st.Effects) effects.Add(e.Clone());
            RebuildRows();
            RefreshPresets(st.Preset);
            SetProject(flp, restoreChecks ? st.Checked : null, restoreChecks ? st.Arrangement : null);
            tabs.Value = st.Tab;
            ShowPage();

            Size min = new Size(D(820), D(560));
            Size want = st.WinW > 0 ? new Size(Math.Max(min.Width, D(st.WinW)), Math.Max(min.Height, D(st.WinH))) : new Size(D(920), D(640));
            Rectangle work = Screen.FromPoint(Cursor.Position).WorkingArea;
            want = new Size(Math.Min(want.Width, work.Width - D(20)), Math.Min(want.Height, work.Height - D(50)));
            MinimumSize = SizeFromClientSize(new Size(Math.Min(min.Width, want.Width), Math.Min(min.Height, want.Height)));
            ClientSize = want;
            DoLayout();
        }

        // ---------------------------------------------------------------- building

        Label Lbl(Control parent, string text, Color color)
        {
            Label l = new Label();
            l.AutoSize = false;
            l.UseMnemonic = false;
            l.AutoEllipsis = true;
            l.TextAlign = ContentAlignment.MiddleLeft;
            l.ForeColor = color;
            l.Text = text;
            parent.Controls.Add(l);
            return l;
        }

        Panel Page()
        {
            Panel p = new Panel();
            p.BackColor = Theme.Card;
            tabCard.Controls.Add(p);
            return p;
        }

        void Build()
        {
            // header
            titleLbl = Lbl(this, "FLP → Video", Theme.Text);
            titleLbl.Font = Theme.Title;
            fileLbl = Lbl(this, "", Theme.Dim);
            arrDd = new Dropdown(new string[0]);
            arrDd.Changed += delegate { if (flp != null && arrDd.Value >= 0) SetSong(flp.Arrangements[arrDd.Value], null); };
            Controls.Add(arrDd);
            tips.SetToolTip(arrDd, "Which playlist arrangement to place.");
            bpmBox = new NumBox(120, 1, 3000, 1, 3);
            bpmBox.ValueChanged += delegate { UpdateSummary(); };
            Controls.Add(bpmBox);
            tips.SetToolTip(bpmBox, "Starting tempo of the project. Change it to re-time everything (tempo automation scales along).");
            bpmUnit = Lbl(this, "BPM", Theme.Faint);
            changeBtn = new Btn("Change FLP…", BtnKind.Secondary);
            changeBtn.Click += delegate { ChangeFlp(); };
            Controls.Add(changeBtn);

            // channels (always visible: every tab applies to the ticked channels)
            chCard = AddCard("Channels");
            allBtn = SmallBtn(chCard, "All");
            allBtn.Click += delegate { SetAllPicked(true); };
            noneBtn = SmallBtn(chCard, "None");
            noneBtn.Click += delegate { SetAllPicked(false); };
            chList = new ChannelList();
            chList.SourceTag = delegate (Part p) { return matched.ContainsKey(p) ? "subclip" : source == null ? "no clip" : ""; };
            chList.PickedChanged += delegate { UpdateSummary(); };
            chCard.Controls.Add(chList);

            // tabs
            tabCard = AddCard("");
            tabs = new TabStrip(TabNames);
            tabs.Changed += delegate { ShowPage(); };
            tabCard.Controls.Add(tabs);
            pages = new Panel[] { Page(), Page(), Page(), Page() };
            Panel place = pages[0], time = pages[1], notes = pages[2], fx = pages[3];

            // -- placement
            clipKey = Lbl(place, "Clip", Theme.Dim);
            clipVal = Lbl(place, "", Theme.Text);
            startKey = Lbl(place, "Start at", Theme.Dim);
            startSeg = new Segmented("Project start", "Cursor", "Custom");
            startSeg.Value = settings.StartMode;
            startSeg.Changed += delegate
            {
                if (startSeg.Value == 2 && customBox.Milliseconds <= 0 && cursorMs > 0) customBox.Milliseconds = cursorMs;
                SyncHints();
            };
            place.Controls.Add(startSeg);
            customBox = new TimeField();
            double custom = TimeField.Parse(settings.CustomStart);
            customBox.Milliseconds = double.IsNaN(custom) ? 0 : custom;
            tips.SetToolTip(customBox, "Where the song's 0:00 lands, e.g. 1:23.456 or 83.456 (seconds).");
            place.Controls.Add(customBox);
            destKey = Lbl(place, "Put on", Theme.Dim);
            destSeg = new Segmented("New tracks", "Selected track");
            destSeg.Value = settings.Destination;
            destSeg.Changed += delegate { SyncHints(); UpdateSummary(); };
            place.Controls.Add(destSeg);
            audioKey = Lbl(place, "Audio", Theme.Dim);
            audioToggle = new Toggle();
            audioToggle.Checked = settings.Audio;
            audioToggle.Changed += delegate { SyncHints(); UpdateSummary(); };
            place.Controls.Add(audioToggle);

            // -- timing
            lenKey = Lbl(time, "Length", Theme.Dim);
            tips.SetToolTip(lenKey, "Shortest and longest clip, in note values. A clip still ends where the next one starts.");
            minDd = new Dropdown(Planner.LengthNames);
            minDd.Value = settings.MinLen;
            time.Controls.Add(minDd);
            tips.SetToolTip(minDd, "Shortest: notes shorter than this are lengthened.");
            toLbl = Lbl(time, "to", Theme.Faint);
            toLbl.TextAlign = ContentAlignment.MiddleCenter;
            maxDd = new Dropdown(Planner.LengthNames);
            maxDd.Value = settings.MaxLen;
            time.Controls.Add(maxDd);
            tips.SetToolTip(maxDd, "Longest: notes longer than this are cut. Set both the same for fixed-length clips.");
            stepKey = Lbl(time, "Step hits", Theme.Dim);
            tips.SetToolTip(stepKey, "How long a channel-rack step lasts. FL stores steps without a length.");
            stepDd = new Dropdown(Planner.StepNames);
            stepDd.Value = settings.StepLen;
            time.Controls.Add(stepDd);
            longKey = Lbl(time, "Long notes", Theme.Dim);
            longSeg = new Segmented("Extend", "Freeze", "Stretch");
            longSeg.Value = settings.LongMode;
            longSeg.Changed += delegate { SyncHints(); };
            time.Controls.Add(longSeg);
            opKey = Lbl(time, "Opacity", Theme.Dim);
            opKey.TextAlign = ContentAlignment.MiddleRight;
            opToggle = new Toggle();
            opToggle.Checked = settings.Velocity;
            opToggle.Changed += delegate { SyncHints(); };
            time.Controls.Add(opToggle);
            mutedKey = Lbl(time, "Muted", Theme.Dim);
            tips.SetToolTip(mutedKey, "Clips that are muted in the playlist, or sit on a muted playlist track.");
            mutedSeg = new Segmented("Skip", "Include");
            mutedSeg.Value = settings.IncludeMuted ? 1 : 0;
            mutedSeg.Changed += delegate { SyncHints(); UpdateSummary(); };
            time.Controls.Add(mutedSeg);
            mutedHint = Lbl(time, "", Theme.Faint);

            // -- notes: slides and pitch
            slideKey = Lbl(notes, "Slides", Theme.Dim);
            tips.SetToolTip(slideKey, "FL slide notes glide the note that's already playing instead of starting a new one.");
            slideSeg = new Segmented(Planner.SlideNames);
            slideSeg.Value = settings.SlideMode;
            slideSeg.Changed += delegate { SyncHints(); UpdateSummary(); DoLayout(); };
            notes.Controls.Add(slideSeg);
            easeKey = Lbl(notes, "Ease", Theme.Dim);
            easeKey.TextAlign = ContentAlignment.MiddleRight;
            easeBox = new NumBox(settings.SlideEaseMs, 0, 5000, 10, 0);
            notes.Controls.Add(easeBox);
            easeUnit = Lbl(notes, "ms", Theme.Faint);
            pitchKey = Lbl(notes, "Pitch", Theme.Dim);
            tips.SetToolTip(pitchKey, "Pitch each clip by how far its note is from the root note.");
            pitchSeg = new Segmented(Planner.PitchNames);
            pitchSeg.Value = settings.PitchMode;
            pitchSeg.Changed += delegate { SyncHints(); UpdateSummary(); };
            notes.Controls.Add(pitchSeg);
            rootKey = Lbl(notes, "Root", Theme.Dim);
            rootKey.TextAlign = ContentAlignment.MiddleRight;
            rootDd = new Dropdown(Planner.RootNames());
            rootDd.Value = settings.Root - Planner.RootLow;
            notes.Controls.Add(rootDd);
            tips.SetToolTip(rootDd, "The note that plays the clip as recorded. FL numbering: C5 is middle C (key 60).");
            poseKey = Lbl(notes, "Slide pose", Theme.Dim);
            poseKey.TextAlign = ContentAlignment.TopLeft;
            slidePose = new PosePanel(tips);
            slidePose.Value = settings.SlidePose;
            notes.Controls.Add(slidePose);
            poseHint = Lbl(notes, "Pick Pop or Toggle to set where a slide sends the picture.", Theme.Faint);
            notesEmpty = Lbl(notes, "Only audio clips in this arrangement — no notes to slide or pitch.", Theme.Faint);

            // -- effects
            presetDd = new Dropdown(new string[0]);
            presetDd.Placeholder = "Presets";
            presetDd.EmptyText = "No saved presets yet";
            presetDd.Changed += delegate { LoadPreset(); };
            fx.Controls.Add(presetDd);
            savePresetBtn = new Btn("Save", BtnKind.Secondary);
            savePresetBtn.Click += delegate { SavePreset(); };
            tips.SetToolTip(savePresetBtn, "Save these effects as a preset.");
            fx.Controls.Add(savePresetBtn);
            delPresetBtn = new Btn("Delete", BtnKind.Secondary);
            delPresetBtn.Click += delegate { DeletePreset(); };
            fx.Controls.Add(delPresetBtn);
            fxPanel = new Panel();
            fxPanel.AutoScroll = true;
            fxPanel.BackColor = Theme.Card;
            fxPanel.Resize += delegate { LayoutRows(); };
            fxPanel.HandleCreated += delegate { Native.DarkScrollbars(fxPanel); };
            fx.Controls.Add(fxPanel);
            fxEmpty = Lbl(fxPanel, "No effects — clips are placed as they are.", Theme.Faint);
            fxEmpty.TextAlign = ContentAlignment.MiddleCenter;
            addFxBtn = new Btn("+  Add effect", BtnKind.Secondary);
            addFxBtn.Click += delegate { ShowAddMenu(); };
            fx.Controls.Add(addFxBtn);

            // footer
            summaryLbl = Lbl(this, "", Theme.Dim);
            cancelBtn = new Btn("Cancel", BtnKind.Secondary);
            cancelBtn.DialogResult = DialogResult.Cancel;
            Controls.Add(cancelBtn);
            goBtn = new Btn("Generate", BtnKind.Primary);
            goBtn.Click += delegate { Generate(); };
            Controls.Add(goBtn);
            AcceptButton = goBtn;
            CancelButton = cancelBtn;

            SyncHints();
        }

        Card AddCard(string title)
        {
            Card c = new Card(title);
            Controls.Add(c);
            return c;
        }

        Btn SmallBtn(Control parent, string text)
        {
            Btn b = new Btn(text, BtnKind.Ghost);
            b.Font = Theme.Small;
            parent.Controls.Add(b);
            return b;
        }

        static string Seconds(double ms)
        {
            return (ms / 1000.0).ToString("0.###", CultureInfo.InvariantCulture) + " s";
        }

        void ShowPage()
        {
            for (int i = 0; i < pages.Length; i++) pages[i].Visible = i == tabs.Value;
        }

        // ---------------------------------------------------------------- layout

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            DoLayout();
            Invalidate();
        }

        /// <summary>One "key  control" row; returns the y of the next row.</summary>
        int RowAt(int y, bool visible, Label key, params Control[] parts)
        {
            key.Visible = visible;
            foreach (Control c in parts) c.Visible = visible;
            if (!visible) return y;
            key.SetBounds(D(PAD), y, D(KEY), D(ROW));
            return y + D(PITCH);
        }

        void DoLayout()
        {
            if (goBtn == null) return;
            int W = ClientSize.Width, H = ClientSize.Height;
            int m = D(M), pad = D(PAD), key = D(KEY), row = D(ROW), gap = D(GAP);

            // header
            changeBtn.SetBounds(W - m - D(116), D(15), D(116), row);
            bpmUnit.SetBounds(changeBtn.Left - D(10) - D(34), D(15), D(34), row);
            bpmBox.SetBounds(bpmUnit.Left - D(4) - D(66), D(15), D(66), row);
            arrDd.SetBounds(bpmBox.Left - D(10) - D(170), D(15), D(170), row);
            int headRight = arrDd.Visible ? arrDd.Left : bpmBox.Left;
            titleLbl.SetBounds(m - D(2), D(7), Math.Max(D(100), headRight - m - D(16)), D(26));
            fileLbl.SetBounds(m, D(32), Math.Max(D(100), headRight - m - D(16)), D(18));

            int top = D(HEAD) + gap, bottom = H - D(FOOT) - gap;
            int bodyH = Math.Max(D(200), bottom - top);

            // channels column
            int leftW = D(LEFT);
            chCard.SetBounds(m, top, leftW, bodyH);
            noneBtn.SetBounds(leftW - pad - D(40) + D(8), D(6), D(40), D(24));
            allBtn.SetBounds(noneBtn.Left - D(2) - D(30), D(6), D(30), D(24));
            chList.SetBounds(D(4), D(TOP), leftW - D(8), bodyH - D(TOP) - D(6));

            // tabbed column
            int x0 = m + leftW + gap, rw = Math.Max(D(460), W - x0 - m);
            tabCard.SetBounds(x0, top, rw, bodyH);
            tabs.SetBounds(D(4), D(2), rw - D(8), D(40));
            int pageTop = D(46), pw = rw - D(2), ph = Math.Max(D(100), bodyH - pageTop - D(4));
            foreach (Panel p in pages) p.SetBounds(D(1), pageTop, pw, ph);
            int valX = pad + key;

            // placement
            int y = D(12);
            clipKey.SetBounds(pad, y, key, row);
            clipVal.SetBounds(valX, y, pw - valX - pad, row);
            y += D(PITCH);
            startKey.SetBounds(pad, y, key, row);
            startSeg.SetBounds(valX, y, D(270), row);
            customBox.SetBounds(startSeg.Right + D(8), y, D(108), row);
            y += D(PITCH);
            destKey.SetBounds(pad, y, key, row);
            destSeg.SetBounds(valX, y, D(230), row);
            audioKey.SetBounds(destSeg.Right + D(18), y, D(44), row);
            audioToggle.SetBounds(audioKey.Right + D(4), y + (row - D(18)) / 2, D(34), D(18));

            // timing
            bool steps = song != null && SongHas(true), muted = song != null && song.HasMuted, notes = song != null && HasNotes();
            y = D(12);
            minDd.SetBounds(valX, y, D(104), row);
            toLbl.SetBounds(minDd.Right, y, D(26), row);
            maxDd.SetBounds(toLbl.Right, y, D(104), row);
            y = RowAt(y, true, lenKey, minDd, toLbl, maxDd);
            stepDd.SetBounds(valX, y, D(150), row);
            y = RowAt(y, steps, stepKey, stepDd);
            longSeg.SetBounds(valX, y, D(222), row);
            opKey.SetBounds(longSeg.Right + D(10), y, D(60), row);
            opToggle.SetBounds(opKey.Right + D(6), y + (row - D(18)) / 2, D(34), D(18));
            y = RowAt(y, true, longKey, longSeg, opKey, opToggle);
            mutedSeg.SetBounds(valX, y, D(150), row);
            mutedHint.SetBounds(mutedSeg.Right + D(12), y, Math.Max(D(40), pw - mutedSeg.Right - D(12) - pad), row);
            RowAt(y, muted, mutedKey, mutedSeg, mutedHint);

            // notes
            bool posed = slideSeg.Value == Planner.SlidePop || slideSeg.Value == Planner.SlideToggle;
            y = D(12);
            slideSeg.SetBounds(valX, y, D(300), row);
            easeKey.SetBounds(slideSeg.Right + D(8), y, D(40), row);
            easeBox.SetBounds(easeKey.Right + D(4), y, D(60), row);
            easeUnit.SetBounds(easeBox.Right + D(4), y, D(24), row);
            y = RowAt(y, notes, slideKey, slideSeg, easeKey, easeBox, easeUnit);
            easeKey.Visible = easeBox.Visible = easeUnit.Visible = notes && posed;
            pitchSeg.SetBounds(valX, y, D(222), row);
            rootKey.SetBounds(pitchSeg.Right + D(10), y, D(46), row);
            rootDd.SetBounds(rootKey.Right + D(6), y, D(82), row);
            y = RowAt(y, notes, pitchKey, pitchSeg, rootKey, rootDd);
            poseKey.SetBounds(pad, y + D(4), key, row);
            poseKey.Visible = notes;
            slidePose.SetBounds(valX, y, pw - valX - pad, Math.Max(D(120), ph - y - D(12)));
            slidePose.Visible = notes && posed;
            poseHint.SetBounds(valX, y, pw - valX - pad, row);
            poseHint.Visible = notes && !posed;
            notesEmpty.SetBounds(pad, D(12), pw - pad * 2, row);
            notesEmpty.Visible = !notes;

            // effects
            delPresetBtn.SetBounds(pw - pad - D(60), D(10), D(60), D(28));
            savePresetBtn.SetBounds(delPresetBtn.Left - D(6) - D(54), D(10), D(54), D(28));
            addFxBtn.SetBounds(pad, D(10), D(120), D(28));
            int presetX = Math.Max(addFxBtn.Right + D(8), savePresetBtn.Left - D(6) - D(180));
            presetDd.SetBounds(presetX, D(10), Math.Max(D(60), savePresetBtn.Left - D(6) - presetX), D(28));
            fxPanel.SetBounds(D(8), D(48), pw - D(16), Math.Max(D(40), ph - D(56)));
            LayoutRows();

            // footer
            goBtn.SetBounds(W - m - D(132), H - D(FOOT) + D(12), D(132), D(34));
            cancelBtn.SetBounds(goBtn.Left - D(8) - D(92), goBtn.Top, D(92), D(34));
            summaryLbl.SetBounds(m, H - D(FOOT) + D(18), Math.Max(D(50), cancelBtn.Left - m - D(16)), D(22));
        }

        bool HasNotes()
        {
            foreach (Part p in song.Parts) if (!p.Clips) return true;
            return false;
        }

        bool SongHas(bool steps)
        {
            foreach (Part p in song.Parts) if (steps ? p.HasSteps : p.HasSlides) return true;
            return false;
        }

        void LayoutRows()
        {
            if (fxPanel == null) return;
            int rowH = D(36), gap = D(4);
            int contentH = rows.Count * (rowH + gap) - (rows.Count > 0 ? gap : 0);
            int w = fxPanel.Width - (contentH > fxPanel.Height ? SystemInformation.VerticalScrollBarWidth + D(2) : 0);
            int y = fxPanel.AutoScrollPosition.Y;
            foreach (EffectRow r in rows)
            {
                r.SetBounds(0, y, Math.Max(D(200), w), rowH);
                y += rowH + gap;
            }
            fxEmpty.SetBounds(0, 0, fxPanel.Width, fxPanel.Height);
            fxEmpty.Visible = rows.Count == 0;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (Pen p = new Pen(Theme.Border))
            {
                e.Graphics.DrawLine(p, 0, D(HEAD), ClientSize.Width, D(HEAD));
                e.Graphics.DrawLine(p, 0, ClientSize.Height - D(FOOT), ClientSize.Width, ClientSize.Height - D(FOOT));
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.DarkTitleBar(Handle);
        }

        // ---------------------------------------------------------------- state

        void SetProject(FlpProject f, List<string> checkedIds, string arrangement)
        {
            flp = f;
            arrDd.Items.Clear();
            foreach (Song s in f.Arrangements) arrDd.Items.Add(s.Arrangement.Length > 0 ? s.Arrangement : "Arrangement");
            int pick = f.Current;
            if (!string.IsNullOrEmpty(arrangement))
                for (int i = 0; i < f.Arrangements.Count; i++)
                    if (f.Arrangements[i].Arrangement == arrangement) pick = i;
            arrDd.SetValueSilently(pick);
            arrDd.Visible = f.Arrangements.Count > 1;
            tips.SetToolTip(changeBtn, f.Path);
            SetSong(f.Arrangements[pick], checkedIds);
        }

        void SetSong(Song s, List<string> checkedIds)
        {
            List<string> keep = checkedIds;
            if (keep == null && song != null)
            {
                keep = new List<string>();
                for (int i = 0; i < picked.Length; i++) if (picked[i]) keep.Add(song.Parts[i].Id);
            }
            song = s;
            matched.Clear();
            foreach (Part p in s.Parts)
            {
                SourceClip c = subclips.Match(p);
                if (c != null) matched[p] = c;
            }
            picked = new bool[s.Parts.Count];
            for (int i = 0; i < picked.Length; i++)
                picked[i] = (keep != null && keep.Contains(s.Parts[i].Id)) || (keep == null && matched.ContainsKey(s.Parts[i]));
            if (s.Parts.Count == 1) picked[0] = true;

            fileLbl.Text = s.FileName + "   ·   " + s.Parts.Count + (s.Parts.Count == 1 ? " channel" : " channels") +
                (s.TempoVaries ? "   ·   tempo automation" : "") +
                (flp != null && flp.Version.Length > 0 ? "   ·   FL " + flp.Version : "");
            bpmBox.Value = Math.Round(s.Bpm, 3);
            bpmShown = bpmBox.Value;
            chList.SetParts(s.Parts, picked);
            UpdateSummary();
            DoLayout();
        }

        void SetAllPicked(bool on)
        {
            for (int i = 0; i < picked.Length; i++) picked[i] = on;
            chList.Invalidate();
            UpdateSummary();
        }

        void ChangeFlp()
        {
            string path = Ui.PickFlp(this, song.Path);
            if (path == null) return;
            try { SetProject(FlpProject.Load(path), null, null); }
            catch (UserError ex) { Ui.Warn(this, ex.Message); }
        }

        void SyncHints()
        {
            // clip line: the timeline clip, plus how many channels found their own subclip
            string clip = source != null ? source.Name + "   ·   " + Seconds(source.LengthMs) : "None selected on the timeline";
            if (source != null && source.SelectedCount > 1) clip += "   ·   first of " + source.SelectedCount;
            if (matched.Count > 0) clip += "   ·   " + matched.Count + (matched.Count == 1 ? " subclip" : " subclips") + " matched";
            clipVal.Text = clip;
            clipVal.ForeColor = source != null || matched.Count > 0 ? Theme.Text : Theme.Danger;
            tips.SetToolTip(clipVal, "Channels use the clip selected on the timeline, unless a Project Media subclip is named after the channel or its sample.");

            bool anyAudio = source != null && source.HasAudio;
            foreach (SourceClip c in matched.Values) anyAudio |= c.HasAudio;
            audioToggle.Enabled = anyAudio;
            tips.SetToolTip(audioToggle, !anyAudio ? "The clip has no audio grouped with it." :
                audioToggle.Checked ? "Copies the clip's audio too, grouped, on a track below each video track." : "Video only.");
            tips.SetToolTip(destSeg, destSeg.Value == 0 ? "One video track per channel, above the clip." :
                targetTrack != null ? "Everything on “" + targetName + "”." : "Select a video track first.");

            tips.SetToolTip(stepDd, stepDd.Value == Planner.StepUntilNext ? "Each step lasts until the channel's next hit." :
                stepDd.Value == Planner.StepSample ? "Each step lasts as long as its sample." : "Each step lasts " + Planner.StepNames[stepDd.Value] + ".");
            tips.SetToolTip(slideSeg,
                slideSeg.Value == Planner.SlideNewClip ? "New clip: slides start their own clip, like normal notes." :
                slideSeg.Value == Planner.SlideContinue ? "Continue: the clip keeps playing through the slide, no cue." :
                slideSeg.Value == Planner.SlidePop ? "Pop: at each slide the picture jumps to the slide pose, then eases back." :
                "Toggle: each slide switches the slide pose on or off, and the state carries on to the next clips.");
            tips.SetToolTip(easeBox, slideSeg.Value == Planner.SlidePop ? "How long a pop takes to settle back." : "How long the switch takes. 0 = instant.");
            tips.SetToolTip(longSeg, LongHints[Math.Max(0, Math.Min(2, longSeg.Value))]);
            tips.SetToolTip(opToggle, opToggle.Checked ? "Opacity follows velocity (loudest note in each channel = 100%)." : "Every clip at full opacity.");
            tips.SetToolTip(pitchSeg, pitchSeg.Value == Planner.PitchShift ? "Shift: the audio is pitched (±24 semitones); clip speed and length stay as they are." :
                pitchSeg.Value == Planner.PitchResample ? "Resample: each clip plays faster or slower to reach its note, video and audio together (like a sampler)." :
                "Off: every clip plays as recorded.");

            if (song != null && song.HasMuted)
            {
                List<string> bits = new List<string>();
                if (song.MutedTracks.Count > 0)
                    bits.Add(song.MutedTracks.Count == 1 ? "“" + song.MutedTracks[0] + "”" : song.MutedTracks.Count + " tracks");
                if (song.MutedClips > 0) bits.Add(song.MutedClips + (song.MutedClips == 1 ? " clip" : " clips"));
                mutedHint.Text = string.Join(" + ", bits.ToArray());
            }
            customBox.Visible = startSeg.Value == 2;
        }

        void UpdateSummary()
        {
            if (goBtn == null || song == null) return;
            SyncHints();
            int channels = 0, clips = 0, orphans = 0;
            for (int i = 0; i < picked.Length; i++)
                if (picked[i])
                {
                    channels++;
                    clips += song.Parts[i].CountHits(mutedSeg.Value == 1);
                    if (source == null && !matched.ContainsKey(song.Parts[i])) orphans++;
                }
            bool canGo = channels > 0 && orphans == 0 && (destSeg.Value == 0 || targetTrack != null);
            goBtn.Enabled = canGo;
            summaryLbl.ForeColor = Theme.Dim;
            if (channels == 0) { summaryLbl.Text = "Tick at least one channel."; return; }
            if (orphans > 0)
            {
                summaryLbl.ForeColor = Theme.Danger;
                summaryLbl.Text = orphans + (orphans == 1 ? " ticked channel has" : " ticked channels have") + " no clip — select one on the timeline.";
                return;
            }
            if (destSeg.Value == 1 && targetTrack == null)
            {
                summaryLbl.ForeColor = Theme.Danger;
                summaryLbl.Text = "Select a video track to place everything on.";
                return;
            }
            string where = destSeg.Value == 0 ? (channels == 1 ? "1 new track" : channels + " new tracks") : "“" + targetName + "”";
            summaryLbl.Text = channels + (channels == 1 ? " channel" : " channels") + "  →  " + where +
                "   ·   " + clips + (clips == 1 ? " clip" : " clips") +
                (pitchSeg.Value == Planner.PitchShift && !(audioToggle.Checked && audioToggle.Enabled) ? "   ·   Shift needs Audio on" : "");
        }

        void Generate()
        {
            CommitFields(this);
            if (!goBtn.Enabled) return;
            DialogResult = DialogResult.OK;
        }

        /// <summary>Applies whatever is typed in any box, even if it still has focus.</summary>
        static void CommitFields(Control root)
        {
            foreach (Control c in root.Controls)
            {
                Field f = c as Field;
                if (f != null) f.CommitNow();
                else CommitFields(c);
            }
        }

        public Job BuildJob()
        {
            Job j = new Job();
            j.Song = song;
            j.Default = source;
            for (int i = 0; i < picked.Length; i++)
                if (picked[i])
                {
                    Part p = song.Parts[i];
                    j.Parts.Add(p);
                    SourceClip c;
                    if (matched.TryGetValue(p, out c)) j.Sources[p] = c;
                }
            j.StartMs = startSeg.Value == 0 ? 0 : startSeg.Value == 1 ? cursorMs : customBox.Milliseconds;
            j.NewTracks = destSeg.Value == 0;
            j.Target = targetTrack;
            j.MinLen = minDd.Value;
            j.MaxLen = maxDd.Value;
            j.StepLen = stepDd.Value;
            j.SlideMode = slideSeg.Value;
            j.SlidePose = slidePose.Value;
            j.SlideEaseMs = easeBox.Value;
            j.PitchMode = pitchSeg.Value;
            j.Root = Planner.RootLow + Math.Max(0, rootDd.Value);
            j.IncludeMuted = mutedSeg.Value == 1;
            j.Audio = audioToggle.Checked && audioToggle.Enabled;
            j.LongMode = longSeg.Value;
            j.Velocity = opToggle.Checked;
            bool retimed = bpmBox.Value > 0 && Math.Abs(bpmBox.Value - bpmShown) > 1e-9;
            j.Scale = retimed ? bpmShown / bpmBox.Value : 1;
            foreach (EffectSpec e in effects) j.Effects.Add(e.Clone());
            return j;
        }

        public void SaveTo(Settings st)
        {
            st.LastFlp = song.Path;
            st.Arrangement = song.Arrangement;
            st.StepLen = stepDd.Value;
            st.SlideMode = slideSeg.Value;
            st.SlidePose = slidePose.Value;
            st.SlideEaseMs = easeBox.Value;
            st.PitchMode = pitchSeg.Value;
            st.Root = Planner.RootLow + Math.Max(0, rootDd.Value);
            st.Tab = tabs.Value;
            st.IncludeMuted = mutedSeg.Value == 1;
            if (audioToggle.Enabled) st.Audio = audioToggle.Checked;
            st.StartMode = startSeg.Value;
            st.CustomStart = TimeField.Format(customBox.Milliseconds);
            st.Destination = destSeg.Value;
            st.MinLen = minDd.Value;
            st.MaxLen = maxDd.Value;
            st.LongMode = longSeg.Value;
            st.Velocity = opToggle.Checked;
            st.Preset = presetDd.Value >= 0 ? presetDd.Items[presetDd.Value] : "";
            st.Checked.Clear();
            for (int i = 0; i < picked.Length; i++) if (picked[i]) st.Checked.Add(song.Parts[i].Id);
            st.Effects.Clear();
            foreach (EffectSpec e in effects) st.Effects.Add(e.Clone());
            if (WindowState == FormWindowState.Normal)
            {
                st.WinW = (int)Math.Round(ClientSize.Width / Theme.S);
                st.WinH = (int)Math.Round(ClientSize.Height / Theme.S);
            }
        }

        // ---------------------------------------------------------------- effects list

        void RebuildRows()
        {
            fxPanel.SuspendLayout();
            foreach (EffectRow r in rows) { fxPanel.Controls.Remove(r); r.Dispose(); }
            rows.Clear();
            fxPanel.AutoScrollPosition = new Point(0, 0);
            foreach (EffectSpec e in effects)
            {
                EffectRow r = new EffectRow(e, tips);
                r.RemoveRequested += delegate (object s, EventArgs a)
                {
                    EffectSpec gone = ((EffectRow)s).Spec;
                    BeginInvoke(new MethodInvoker(delegate { effects.Remove(gone); RebuildRows(); }));
                };
                rows.Add(r);
                fxPanel.Controls.Add(r);
            }
            LayoutRows();
            fxPanel.ResumeLayout();
        }

        void ShowAddMenu()
        {
            ContextMenuStrip menu = Theme.MakeMenu();
            for (int g = 0; g < FxInfo.Groups.Length; g++)
            {
                if (g > 0) menu.Items.Add(new ToolStripSeparator());
                ToolStripLabel head = new ToolStripLabel(FxInfo.GroupNames[g]);
                head.Font = Theme.Caption;
                head.ForeColor = Theme.Faint;
                head.Padding = new Padding(D(4), D(4), 0, 0);
                menu.Items.Add(head);
                foreach (Fx type in FxInfo.Groups[g])
                {
                    Fx t = type;
                    bool used = false;
                    foreach (EffectSpec e in effects) if (e.Type == t) used = true;
                    ToolStripMenuItem item = new ToolStripMenuItem(FxInfo.Name(t));
                    item.Enabled = !used;
                    item.ShortcutKeyDisplayString = used ? "already added" : FxInfo.Short(t);
                    item.ShowShortcutKeys = true;
                    item.Padding = new Padding(D(6), D(3), D(6), D(3));
                    item.Click += delegate
                    {
                        effects.Add(EffectSpec.Create(t));
                        RebuildRows();
                        fxPanel.ScrollControlIntoView(rows[rows.Count - 1]);
                        LayoutRows();
                    };
                    menu.Items.Add(item);
                }
            }
            menu.ShowItemToolTips = false;
            menu.Closed += delegate { BeginInvoke(new MethodInvoker(menu.Dispose)); };
            menu.Show(addFxBtn, new Point(0, addFxBtn.Height + D(2)));
        }

        // ---------------------------------------------------------------- presets

        void RefreshPresets(string select)
        {
            List<string> names = Settings.PresetNames();
            presetDd.Items.Clear();
            presetDd.Items.AddRange(names);
            presetDd.SetValueSilently(select == null ? -1 : names.FindIndex(delegate (string n) { return string.Equals(n, select, StringComparison.CurrentCultureIgnoreCase); }));
            delPresetBtn.Enabled = presetDd.Value >= 0;
        }

        void LoadPreset()
        {
            delPresetBtn.Enabled = presetDd.Value >= 0;
            if (presetDd.Value < 0) return;
            try
            {
                List<EffectSpec> list = Settings.LoadPreset(presetDd.Items[presetDd.Value]);
                effects.Clear();
                effects.AddRange(list);
                RebuildRows();
            }
            catch (Exception ex) { Ui.Warn(this, "Couldn't load that preset.\n\n" + ex.Message); }
        }

        void SavePreset()
        {
            CommitFields(fxPanel);
            string current = presetDd.Value >= 0 ? presetDd.Items[presetDd.Value] : "";
            string name = InputDialog.Ask(this, "Save preset", "Preset name", current.Length > 0 ? current : "My preset");
            if (name == null) return;
            name = Settings.CleanName(name);
            if (name.Length == 0) return;
            if (!string.Equals(name, current, StringComparison.CurrentCultureIgnoreCase) && Settings.PresetExists(name) &&
                !Ui.Confirm(this, "Replace the preset \u201C" + name + "\u201D?")) return;
            try
            {
                Settings.SavePreset(name, effects);
                RefreshPresets(name);
            }
            catch (Exception ex) { Ui.Warn(this, "Couldn't save the preset.\n\n" + ex.Message); }
        }

        void DeletePreset()
        {
            if (presetDd.Value < 0) return;
            string name = presetDd.Items[presetDd.Value];
            if (!Ui.Confirm(this, "Delete the preset \u201C" + name + "\u201D?")) return;
            try { Settings.DeletePreset(name); }
            catch (Exception ex) { Ui.Warn(this, "Couldn't delete the preset.\n\n" + ex.Message); }
            RefreshPresets(null);
        }
    }

    // =========================================================================================
    //  Small windows
    // =========================================================================================

    internal sealed class ProgressWindow : Form
    {
        readonly int total;
        int done, lastPaint;
        bool cancelled;
        readonly Btn stopBtn;

        public bool Cancelled { get { return cancelled; } }

        public ProgressWindow(int total)
        {
            this.total = Math.Max(1, total);
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = false;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Theme.Card;
            Font = Theme.Base;
            DoubleBuffered = true;
            ClientSize = new Size(Theme.D(400), Theme.D(128));
            stopBtn = new Btn("Stop", BtnKind.Secondary);
            stopBtn.SetBounds(ClientSize.Width - Theme.D(16) - Theme.D(84), ClientSize.Height - Theme.D(16) - Theme.D(32), Theme.D(84), Theme.D(32));
            stopBtn.Click += delegate { cancelled = true; stopBtn.Enabled = false; stopBtn.Text = "Stopping\u2026"; };
            Controls.Add(stopBtn);
        }

        /// <summary>Counts one clip; returns true when the user pressed Stop.</summary>
        public bool Step()
        {
            done++;
            int now = Environment.TickCount;
            if (unchecked(now - lastPaint) > 50 || done >= total)
            {
                lastPaint = now;
                Invalidate();
                Application.DoEvents();
            }
            return cancelled;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            int pad = Theme.D(18);
            using (Pen p = new Pen(Theme.InputBorder)) g.DrawRectangle(p, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
            TextRenderer.DrawText(g, cancelled ? "Stopping\u2026" : "Placing clips\u2026", Theme.Semi, new Point(pad, pad), Theme.Text, TextFormatFlags.NoPrefix);
            string count = Math.Min(done, total) + " of " + total;
            Size cs = TextRenderer.MeasureText(count, Theme.Base);
            TextRenderer.DrawText(g, count, Theme.Base, new Point(ClientSize.Width - pad - cs.Width, pad), Theme.Dim, TextFormatFlags.NoPrefix);
            Rectangle bar = new Rectangle(pad, Theme.D(50), ClientSize.Width - pad * 2, Theme.D(8));
            Theme.FillRound(g, bar, Theme.D(4), Theme.Input, Theme.Input);
            int w = (int)Math.Round(bar.Width * Math.Min(1.0, done / (double)total));
            if (w > Theme.D(8)) Theme.FillRound(g, new Rectangle(bar.X, bar.Y, w, bar.Height), Theme.D(4), Theme.Accent, Theme.Accent);
            TextRenderer.DrawText(g, "One Ctrl+Z undoes the whole run.", Theme.Small, new Point(pad, ClientSize.Height - Theme.D(16) - Theme.D(24)), Theme.Faint, TextFormatFlags.NoPrefix);
        }
    }

    internal sealed class InputDialog : Form
    {
        readonly Field box;

        InputDialog(string title, string prompt, string initial)
        {
            Text = title;
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ShowIcon = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Theme.Card;
            ForeColor = Theme.Text;
            Font = Theme.Base;
            ClientSize = new Size(Theme.D(360), Theme.D(142));
            Label l = new Label();
            l.AutoSize = false;
            l.Text = prompt;
            l.ForeColor = Theme.Dim;
            l.SetBounds(Theme.D(18), Theme.D(14), Theme.D(320), Theme.D(22));
            Controls.Add(l);
            box = new Field();
            box.Text = initial;
            box.SetBounds(Theme.D(18), Theme.D(40), Theme.D(324), Theme.D(32));
            Controls.Add(box);
            Btn ok = new Btn("Save", BtnKind.Primary);
            ok.DialogResult = DialogResult.OK;
            ok.SetBounds(Theme.D(342) - Theme.D(96), Theme.D(92), Theme.D(96), Theme.D(34));
            Controls.Add(ok);
            Btn cancel = new Btn("Cancel", BtnKind.Secondary);
            cancel.DialogResult = DialogResult.Cancel;
            cancel.SetBounds(ok.Left - Theme.D(8) - Theme.D(90), ok.Top, Theme.D(90), Theme.D(34));
            Controls.Add(cancel);
            AcceptButton = ok;
            CancelButton = cancel;
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Native.DarkTitleBar(Handle); }
        protected override void OnShown(EventArgs e) { base.OnShown(e); box.FocusAndSelectAll(); }

        public static string Ask(IWin32Window owner, string title, string prompt, string initial)
        {
            using (InputDialog d = new InputDialog(title, prompt, initial))
                return d.ShowDialog(owner) == DialogResult.OK ? d.box.Text : null;
        }
    }

    // =========================================================================================
    //  Theme + controls (all hand-painted so the whole window stays dark)
    // =========================================================================================

    internal static class Theme
    {
        public static float S = 1f;
        // Black and white, with VEGAS's blue-grey as the only tint.
        public static readonly Color Bg = Rgb(0x111214), Card = Rgb(0x18191C), Row = Rgb(0x202226), Border = Rgb(0x26282C),
            Input = Rgb(0x1E2023), InputHover = Rgb(0x272A2E), InputBorder = Rgb(0x32353A), Raised = Rgb(0x3A3E44),
            Text = Rgb(0xF1F2F4), Dim = Rgb(0xA2A7AE), Faint = Rgb(0x6B7078),
            Accent = Rgb(0x8EA3B9), AccentHover = Rgb(0xA3B5C8), AccentDown = Rgb(0x7A8FA5), AccentSoft = Rgb(0x232A32),
            Primary = Rgb(0xECEEF1), PrimaryHover = Rgb(0xFFFFFF), PrimaryDown = Rgb(0xD0D4D9),
            Danger = Rgb(0xE07A72);
        public static Font Base, Semi, Small, Caption, Title;
        static bool ready;

        public static void Init()
        {
            if (ready) return;
            try { using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) S = Math.Max(1f, g.DpiX / 96f); }
            catch { S = 1f; }
            Base = MakeFont("Segoe UI", 9f, false);
            Semi = MakeFont("Segoe UI Semibold", 9f, true);
            Small = MakeFont("Segoe UI", 8.25f, false);
            Caption = MakeFont("Segoe UI Semibold", 7.5f, true);
            Title = MakeFont("Segoe UI Semibold", 13f, true);
            ready = true;
        }

        static Font MakeFont(string family, float size, bool semibold)
        {
            Font f = new Font(family, size, FontStyle.Regular, GraphicsUnit.Point);
            if (string.Equals(f.Name, family, StringComparison.OrdinalIgnoreCase)) return f;
            f.Dispose(); // "Segoe UI Semibold" missing: fall back to bold
            return new Font("Segoe UI", size, semibold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Point);
        }

        public static int D(double v) { return (int)Math.Round(v * S); }

        static Color Rgb(int rgb) { return Color.FromArgb(255, (rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255); }

        public static GraphicsPath RoundPath(RectangleF r, float radius)
        {
            GraphicsPath p = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d < 1) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void FillRound(Graphics g, Rectangle r, int radius, Color fill, Color border)
        {
            if (r.Width <= 1 || r.Height <= 1) return;
            SmoothingMode old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath p = RoundPath(new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1f, r.Height - 1f), radius))
            {
                using (SolidBrush b = new SolidBrush(fill)) g.FillPath(b, p);
                if (border != fill) using (Pen pen = new Pen(border)) g.DrawPath(pen, p);
            }
            g.SmoothingMode = old;
        }

        public static void Lines(Graphics g, Color color, float width, params PointF[] pts)
        {
            SmoothingMode old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (Pen p = new Pen(color, width))
            {
                p.StartCap = LineCap.Round;
                p.EndCap = LineCap.Round;
                p.LineJoin = LineJoin.Round;
                g.DrawLines(p, pts);
            }
            g.SmoothingMode = old;
        }

        public static void Chevron(Graphics g, Rectangle r, Color c)
        {
            float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f, s = 3.5f * S;
            Lines(g, c, 1.5f * S, new PointF(cx - s, cy - s / 2), new PointF(cx, cy + s / 2), new PointF(cx + s, cy - s / 2));
        }

        public static ContextMenuStrip MakeMenu()
        {
            ContextMenuStrip m = new ContextMenuStrip();
            m.Renderer = new DarkMenuRenderer();
            m.Font = Base;
            m.BackColor = Card;
            m.ForeColor = Text;
            m.ShowImageMargin = false;
            m.ShowCheckMargin = false;
            m.Padding = new Padding(D(4), D(4), D(4), D(4));
            return m;
        }

        public static ToolTip MakeToolTip()
        {
            ToolTip t = new ToolTip();
            t.OwnerDraw = true;
            t.InitialDelay = 500;
            t.AutoPopDelay = 12000;
            t.Popup += delegate (object s, PopupEventArgs e)
            {
                string text = t.GetToolTip(e.AssociatedControl);
                Size sz = TextRenderer.MeasureText(text, Small, new Size(D(320), 1000), TextFormatFlags.WordBreak);
                e.ToolTipSize = new Size(sz.Width + D(16), sz.Height + D(12));
            };
            t.Draw += delegate (object s, DrawToolTipEventArgs e)
            {
                using (SolidBrush b = new SolidBrush(Raised)) e.Graphics.FillRectangle(b, e.Bounds);
                using (Pen p = new Pen(InputBorder)) e.Graphics.DrawRectangle(p, 0, 0, e.Bounds.Width - 1, e.Bounds.Height - 1);
                Rectangle r = new Rectangle(D(8), D(6), e.Bounds.Width - D(16), e.Bounds.Height - D(12));
                TextRenderer.DrawText(e.Graphics, e.ToolTipText, Small, r, Text, TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            };
            return t;
        }
    }

    internal sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer() : base(new DarkMenuColors()) { RoundedEdges = false; }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using (SolidBrush b = new SolidBrush(Theme.Card)) e.Graphics.FillRectangle(b, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using (Pen p = new Pen(Theme.InputBorder)) e.Graphics.DrawRectangle(p, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        }

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            Rectangle r = new Rectangle(Theme.D(2), 0, e.Item.Width - Theme.D(4), e.Item.Height);
            Theme.FillRound(e.Graphics, r, Theme.D(4), Theme.InputHover, Theme.InputHover);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            ToolStripMenuItem mi = e.Item as ToolStripMenuItem;
            bool secondColumn = mi != null && !string.IsNullOrEmpty(mi.ShortcutKeyDisplayString) && e.Text == mi.ShortcutKeyDisplayString;
            if (e.Item is ToolStripLabel || secondColumn) e.TextColor = Theme.Faint;
            else if (!e.Item.Enabled) e.TextColor = Theme.Faint;
            else e.TextColor = mi != null && mi.Checked ? Theme.Accent : Theme.Text;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e) { }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2;
            using (Pen p = new Pen(Theme.Border)) e.Graphics.DrawLine(p, Theme.D(6), y, e.Item.Width - Theme.D(6), y);
        }
    }

    internal sealed class DarkMenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground { get { return Theme.Card; } }
        public override Color ImageMarginGradientBegin { get { return Theme.Card; } }
        public override Color ImageMarginGradientMiddle { get { return Theme.Card; } }
        public override Color ImageMarginGradientEnd { get { return Theme.Card; } }
        public override Color MenuBorder { get { return Theme.InputBorder; } }
        public override Color MenuItemBorder { get { return Theme.InputHover; } }
        public override Color MenuItemSelected { get { return Theme.InputHover; } }
        public override Color SeparatorDark { get { return Theme.Border; } }
        public override Color SeparatorLight { get { return Theme.Border; } }
    }

    internal static class Native
    {
        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        static extern int SetWindowTheme(IntPtr hwnd, string subAppName, string subIdList);
        [DllImport("user32.dll")]
        static extern bool EnableWindow(IntPtr hwnd, bool enable);

        public static void DarkTitleBar(IntPtr hwnd)
        {
            try
            {
                int on = 1;
                if (DwmSetWindowAttribute(hwnd, 20, ref on, 4) != 0) DwmSetWindowAttribute(hwnd, 19, ref on, 4);
            }
            catch { } // older Windows: keep the normal title bar
        }

        public static void DarkScrollbars(Control c)
        {
            try { SetWindowTheme(c.Handle, "DarkMode_Explorer", null); } catch { }
        }

        public static IntPtr VegasHandle()
        {
            try { return Process.GetCurrentProcess().MainWindowHandle; } catch { return IntPtr.Zero; }
        }

        public static IWin32Window VegasWindow()
        {
            IntPtr h = VegasHandle();
            return h == IntPtr.Zero ? null : new WindowHandle(h);
        }

        public static void Enable(IntPtr hwnd, bool on)
        {
            if (hwnd == IntPtr.Zero) return;
            try { EnableWindow(hwnd, on); } catch { }
        }

        sealed class WindowHandle : IWin32Window
        {
            readonly IntPtr handle;
            public WindowHandle(IntPtr h) { handle = h; }
            public IntPtr Handle { get { return handle; } }
        }
    }

    /// <summary>Base for hand-painted controls.</summary>
    internal abstract class Painted : Control
    {
        protected bool Hover, Pressed;

        protected Painted()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            Font = Theme.Base;
            ForeColor = Theme.Text;
            Cursor = Cursors.Hand;
            TabStop = true;
        }

        protected Color Behind { get { return Parent != null ? Parent.BackColor : Theme.Bg; } }

        protected override void OnMouseEnter(EventArgs e) { Hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { Hover = false; Pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { Pressed = true; Invalidate(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { Pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    }

    internal enum BtnKind { Primary, Secondary, Ghost }

    internal sealed class Btn : Painted, IButtonControl
    {
        public BtnKind Kind;
        DialogResult result = DialogResult.None;

        public Btn(string text, BtnKind kind) { Text = text; Kind = kind; }

        public DialogResult DialogResult { get { return result; } set { result = value; } }
        public void NotifyDefault(bool value) { }
        public void PerformClick() { if (Enabled && Visible) OnClick(EventArgs.Empty); }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            Form f = FindForm();
            if (f != null && result != DialogResult.None) f.DialogResult = result;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space) { PerformClick(); e.Handled = true; }
            base.OnKeyDown(e);
        }

        protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Behind);
            Color bg, fg, border;
            if (Kind == BtnKind.Primary)
            {
                bg = !Enabled ? Theme.Input : Pressed ? Theme.PrimaryDown : Hover ? Theme.PrimaryHover : Theme.Primary;
                fg = Enabled ? Theme.Bg : Theme.Faint;
                border = bg;
            }
            else if (Kind == BtnKind.Secondary)
            {
                bg = !Enabled ? Theme.Card : Pressed ? Theme.Input : Hover ? Theme.InputHover : Theme.Input;
                fg = Enabled ? Theme.Text : Theme.Faint;
                border = Enabled ? Theme.InputBorder : Theme.Border;
            }
            else
            {
                bg = Enabled && (Hover || Pressed) ? Theme.InputHover : Behind;
                fg = !Enabled ? Theme.Faint : Hover ? Theme.Text : Theme.Dim;
                border = bg;
            }
            if (Focused && ShowFocusCues && Enabled) border = Theme.Accent;
            Theme.FillRound(g, ClientRectangle, Theme.D(5), bg, border);
            TextRenderer.DrawText(g, Text, Kind == BtnKind.Primary ? Theme.Semi : Font, ClientRectangle, fg,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }

    /// <summary>Small round "x" (remove) button.</summary>
    internal sealed class IconBtn : Painted
    {
        public IconBtn() { TabStop = false; }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Behind);
            if (Hover) Theme.FillRound(g, ClientRectangle, Theme.D(6), Theme.InputHover, Theme.InputHover);
            Color c = Hover ? Theme.Danger : Theme.Dim;
            float cx = Width / 2f, cy = Height / 2f, s = 4f * Theme.S;
            Theme.Lines(g, c, 1.6f * Theme.S, new PointF(cx - s, cy - s), new PointF(cx + s, cy + s));
            Theme.Lines(g, c, 1.6f * Theme.S, new PointF(cx + s, cy - s), new PointF(cx - s, cy + s));
        }
    }

    /// <summary>Row of mutually exclusive options.</summary>
    internal sealed class Segmented : Painted
    {
        readonly string[] items;
        int sel, hot = -1;
        public event EventHandler Changed;

        public Segmented(params string[] items) { this.items = items; }

        public int Value
        {
            get { return sel; }
            set
            {
                int v = Math.Max(0, Math.Min(items.Length - 1, value));
                if (v == sel) return;
                sel = v;
                Invalidate();
                if (Changed != null) Changed(this, EventArgs.Empty);
            }
        }

        Rectangle Slot(int i)
        {
            int w = Width / items.Length;
            return new Rectangle(i * w, 0, i == items.Length - 1 ? Width - i * w : w, Height);
        }

        int SlotAt(Point p)
        {
            for (int i = 0; i < items.Length; i++) if (Slot(i).Contains(p)) return i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = SlotAt(e.Location);
            if (h != hot) { hot = h; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { hot = -1; base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            int i = SlotAt(e.Location);
            if (i >= 0 && e.Button == MouseButtons.Left) { Focus(); Value = i; }
            base.OnMouseDown(e);
        }

        protected override bool IsInputKey(Keys keyData)
        {
            return keyData == Keys.Left || keyData == Keys.Right || base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Left) Value = sel - 1;
            else if (e.KeyCode == Keys.Right) Value = sel + 1;
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Behind);
            Theme.FillRound(g, ClientRectangle, Theme.D(7), Theme.Input, Focused && ShowFocusCues ? Theme.Accent : Theme.InputBorder);
            int inset = Theme.D(3);
            for (int i = 0; i < items.Length; i++)
            {
                Rectangle r = Slot(i);
                Rectangle inner = new Rectangle(r.X + inset, r.Y + inset, r.Width - inset * 2, r.Height - inset * 2);
                Color fg = Theme.Dim;
                if (i == sel)
                {
                    Theme.FillRound(g, inner, Theme.D(5), Theme.Raised, Theme.Raised);
                    fg = Color.White;
                }
                else if (i == hot)
                {
                    Theme.FillRound(g, inner, Theme.D(5), Theme.InputHover, Theme.InputHover);
                    fg = Theme.Text;
                }
                TextRenderer.DrawText(g, items[i], i == sel ? Theme.Semi : Theme.Base, r, fg,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
                    TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }
        }
    }

    internal sealed class Toggle : Painted
    {
        bool on;
        public event EventHandler Changed;

        public bool Checked
        {
            get { return on; }
            set { if (value == on) return; on = value; Invalidate(); if (Changed != null) Changed(this, EventArgs.Empty); }
        }

        protected override void OnClick(EventArgs e) { Focus(); Checked = !on; base.OnClick(e); }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space) { Checked = !on; e.Handled = true; }
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Behind);
            // on = white track with a black knob, off = dark track with a grey knob
            Color track = !Enabled ? Theme.Input : on ? (Hover ? Theme.PrimaryHover : Theme.Primary) : (Hover ? Theme.Raised : Theme.InputBorder);
            Theme.FillRound(g, ClientRectangle, Height / 2, track, Focused && ShowFocusCues ? Theme.Accent : track);
            int k = Height - Theme.D(6);
            int x = on ? Width - Theme.D(3) - k : Theme.D(3);
            SmoothingMode old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (SolidBrush b = new SolidBrush(!Enabled ? Theme.Faint : on ? Theme.Bg : Theme.Dim)) g.FillEllipse(b, x, Theme.D(3), k, k);
            g.SmoothingMode = old;
        }
    }

    /// <summary>Dark drop-down list (opens a dark menu).</summary>
    internal sealed class Dropdown : Painted
    {
        public readonly List<string> Items = new List<string>();
        public string Placeholder = "", EmptyText = "Nothing here";
        int sel = -1;
        public event EventHandler Changed;

        public Dropdown(string[] items)
        {
            Items.AddRange(items);
            if (Items.Count > 0) sel = 0;
        }

        public int Value
        {
            get { return sel; }
            set
            {
                int v = value < -1 ? -1 : value >= Items.Count ? Items.Count - 1 : value;
                if (v == sel) return;
                sel = v;
                Invalidate();
                if (Changed != null) Changed(this, EventArgs.Empty);
            }
        }

        public void SetValueSilently(int v)
        {
            sel = v < -1 ? -1 : v >= Items.Count ? Items.Count - 1 : v;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left) Open();
        }

        protected override bool IsInputKey(Keys keyData)
        {
            return keyData == Keys.Up || keyData == Keys.Down || base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Down && Items.Count > 0) Value = Math.Min(Items.Count - 1, sel + 1);
            else if (e.KeyCode == Keys.Up && Items.Count > 0) Value = Math.Max(0, sel - 1);
            else if (e.KeyCode == Keys.Space || e.KeyCode == Keys.F4) Open();
            base.OnKeyDown(e);
        }

        void Open()
        {
            Focus();
            ContextMenuStrip menu = Theme.MakeMenu();
            menu.MinimumSize = new Size(Width, 0);
            if (Items.Count == 0)
            {
                ToolStripMenuItem none = new ToolStripMenuItem(EmptyText);
                none.Enabled = false;
                menu.Items.Add(none);
            }
            for (int i = 0; i < Items.Count; i++)
            {
                int index = i;
                ToolStripMenuItem item = new ToolStripMenuItem(Items[i]);
                item.Checked = i == sel;
                item.Padding = new Padding(Theme.D(4), Theme.D(3), Theme.D(4), Theme.D(3));
                item.Click += delegate { Value = index; };
                menu.Items.Add(item);
            }
            menu.Closed += delegate { BeginInvoke(new MethodInvoker(menu.Dispose)); };
            menu.Show(this, new Point(0, Height + Theme.D(2)));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Behind);
            Color border = Focused && ShowFocusCues ? Theme.Accent : Hover && Enabled ? Theme.Raised : Theme.InputBorder;
            Theme.FillRound(g, ClientRectangle, Theme.D(6), Enabled ? (Hover ? Theme.InputHover : Theme.Input) : Theme.Card, border);
            bool empty = sel < 0 || sel >= Items.Count;
            string text = empty ? Placeholder : Items[sel];
            Rectangle tr = new Rectangle(Theme.D(10), 0, Width - Theme.D(34), Height);
            TextRenderer.DrawText(g, text, Font, tr, !Enabled ? Theme.Faint : empty ? Theme.Dim : Theme.Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            Theme.Chevron(g, new Rectangle(Width - Theme.D(26), 0, Theme.D(16), Height), Theme.Dim);
        }
    }

    internal sealed class InnerTextBox : TextBox
    {
        protected override bool IsInputKey(Keys keyData)
        {
            Keys k = keyData & Keys.KeyCode;
            return k == Keys.Enter || k == Keys.Up || k == Keys.Down || base.IsInputKey(keyData);
        }

        protected override void OnKeyPress(KeyPressEventArgs e)
        {
            if (e.KeyChar == '\r') e.Handled = true; // no beep
            base.OnKeyPress(e);
        }
    }

    /// <summary>Dark single-line text box.</summary>
    internal class Field : Control
    {
        protected readonly InnerTextBox Box;

        public Field()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Box = new InnerTextBox();
            Box.BorderStyle = BorderStyle.None;
            Box.BackColor = Theme.Input;
            Box.ForeColor = Theme.Text;
            Box.Font = Theme.Base;
            Box.GotFocus += delegate { Invalidate(); };
            Box.LostFocus += delegate { Commit(); Invalidate(); };
            Box.KeyDown += BoxKeyDown;
            Controls.Add(Box);
            Cursor = Cursors.IBeam;
        }

        public override string Text
        {
            get { return Box == null ? "" : Box.Text; }
            set { if (Box != null) Box.Text = value; }
        }

        public void FocusAndSelectAll() { Box.Focus(); Box.SelectAll(); }

        protected Color Behind { get { return Parent != null ? Parent.BackColor : Theme.Bg; } }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int pad = Theme.D(8);
            Box.SetBounds(pad, Math.Max(0, (Height - Box.Height) / 2), Math.Max(1, Width - pad * 2), Box.Height);
        }

        protected override void OnMouseDown(MouseEventArgs e) { Box.Focus(); base.OnMouseDown(e); }

        protected override void OnEnabledChanged(EventArgs e)
        {
            Box.BackColor = Enabled ? Theme.Input : Theme.Card;
            Invalidate();
            base.OnEnabledChanged(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Behind);
            Theme.FillRound(e.Graphics, ClientRectangle, Theme.D(6), Enabled ? Theme.Input : Theme.Card,
                Box.Focused ? Theme.Accent : Enabled ? Theme.InputBorder : Theme.Border);
        }

        protected virtual void Commit() { }

        public void CommitNow() { Commit(); }

        protected virtual void BoxKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter) { Commit(); Box.SelectAll(); e.Handled = true; e.SuppressKeyPress = true; }
        }
    }

    /// <summary>Number box: type, arrow keys, or mouse wheel (Shift = x10).</summary>
    internal sealed class NumBox : Field
    {
        double current, min, max, step;
        readonly int decimals;
        public event EventHandler ValueChanged;

        public NumBox(double value, double min, double max, double step, int decimals)
        {
            this.min = min;
            this.max = max;
            this.step = step;
            this.decimals = decimals;
            Box.TextAlign = HorizontalAlignment.Center;
            Box.MouseWheel += delegate (object s, MouseEventArgs e)
            {
                if (!Box.Focused) return;
                Commit();
                Set(current + Math.Sign(e.Delta) * this.step * (ModifierKeys == Keys.Shift ? 10 : 1), true);
                HandledMouseEventArgs h = e as HandledMouseEventArgs;
                if (h != null) h.Handled = true;
            };
            Set(value, false);
            Box.Text = Format(current);
        }

        public double Value { get { return current; } set { Set(value, true); } }

        void Set(double v, bool notify)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) v = current;
            v = Math.Round(Math.Max(min, Math.Min(max, v)), decimals);
            bool changed = v != current;
            current = v;
            if (Box != null) Box.Text = Format(v);
            if (changed && notify && ValueChanged != null) ValueChanged(this, EventArgs.Empty);
        }

        string Format(double v)
        {
            return v.ToString(decimals > 0 ? "0." + new string('#', decimals) : "0", CultureInfo.InvariantCulture);
        }

        protected override void Commit()
        {
            double v;
            if (double.TryParse(Box.Text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v)) Set(v, true);
            else Box.Text = Format(current);
        }

        protected override void BoxKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Up || e.KeyCode == Keys.Down)
            {
                Commit();
                Set(current + (e.KeyCode == Keys.Up ? step : -step) * (e.Shift ? 10 : 1), true);
                Box.SelectAll();
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }
            base.BoxKeyDown(sender, e);
        }
    }

    /// <summary>Time box: accepts 1:23.456, 83.456 or 1:02:03.5.</summary>
    internal sealed class TimeField : Field
    {
        double ms;

        public TimeField() { Box.TextAlign = HorizontalAlignment.Center; Box.Text = Format(0); }

        public double Milliseconds
        {
            get { return ms; }
            set { ms = Math.Max(0, value); Box.Text = Format(ms); }
        }

        protected override void Commit()
        {
            double v = Parse(Box.Text);
            if (!double.IsNaN(v)) ms = v;
            Box.Text = Format(ms);
        }

        public static double Parse(string s)
        {
            if (string.IsNullOrEmpty(s)) return double.NaN;
            string[] parts = s.Trim().Replace(',', '.').Split(':');
            if (parts.Length > 3) return double.NaN;
            double total = 0;
            foreach (string part in parts)
            {
                double x;
                if (!double.TryParse(part.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out x) || x < 0) return double.NaN;
                total = total * 60 + x;
            }
            return total * 1000;
        }

        public static string Format(double ms)
        {
            long t = (long)Math.Round(Math.Max(0, ms));
            long h = t / 3600000, m = t / 60000 % 60, s = t / 1000 % 60, f = t % 1000;
            return h > 0 ? string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}.{3:000}", h, m, s, f)
                         : string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}.{2:000}", m, s, f);
        }
    }

    /// <summary>Rounded section with a small caption.</summary>
    internal sealed class Card : Panel
    {
        readonly string title;

        public Card(string title)
        {
            this.title = title.ToUpperInvariant();
            BackColor = Theme.Card;
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent != null ? Parent.BackColor : Theme.Bg);
            Theme.FillRound(e.Graphics, ClientRectangle, Theme.D(6), Theme.Card, Theme.Border);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            TextRenderer.DrawText(e.Graphics, title, Theme.Caption, new Point(Theme.D(14), Theme.D(12)), Theme.Faint, TextFormatFlags.NoPrefix);
        }
    }

    /// <summary>Reference picture for the pose editors: a frame of the selected clip, rendered by VEGAS once.</summary>
    internal static class FrameRef
    {
        public static int Width = 1920, Height = 1080;
        public static Func<Image> Loader;
        static Image cached;
        static bool tried;

        public static Image Image
        {
            get
            {
                if (!tried)
                {
                    tried = true;
                    try { if (Loader != null) cached = Loader(); } catch { cached = null; }
                }
                return cached;
            }
        }
    }

    /// <summary>
    /// VEGAS Pan/Crop, in miniature: the dotted box is the camera. Drag inside it to move, drag a handle to
    /// scale, drag outside it to rotate (Shift snaps to 15 degrees), scroll to zoom, double-click to reset.
    /// The inset shows the result.
    /// </summary>
    internal sealed class PoseEditor : Control
    {
        Pose pose = Pose.Identity;
        public event EventHandler PoseChanged;

        enum Drag { None, Move, Scale, Rotate }
        Drag drag;
        Pose start;
        PointF startPt;
        double startDist, startAngle;

        public PoseEditor()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            BackColor = Theme.Input;
        }

        public Pose Value
        {
            get { return pose; }
            set { pose = value; Invalidate(); }
        }

        void Set(Pose p)
        {
            p.Zoom = Math.Max(5, Math.Min(2000, p.Zoom));
            pose = p;
            Invalidate();
            if (PoseChanged != null) PoseChanged(this, EventArgs.Empty);
        }

        static double FW { get { return Math.Max(16, FrameRef.Width); } }
        static double FH { get { return Math.Max(16, FrameRef.Height); } }

        // ---- geometry: frame coordinates (project pixels, y down) <-> screen

        float ViewScale { get { return (float)Math.Max(0.01, Math.Min((Width - 24) / (FW * 1.8), (Height - 24) / (FH * 1.8))); } }

        PointF Origin
        {
            get { float s = ViewScale; return new PointF(Width / 2f - (float)(FW * s / 2), Height / 2f - (float)(FH * s / 2)); }
        }

        PointF ToScreen(double x, double y) { PointF o = Origin; float s = ViewScale; return new PointF(o.X + (float)x * s, o.Y + (float)y * s); }
        PointF ToFrame(Point p) { PointF o = Origin; float s = ViewScale; return new PointF((p.X - o.X) / s, (p.Y - o.Y) / s); }

        double BoxW(Pose p) { return FW * 100 / p.Zoom; }
        double BoxH(Pose p) { return FH * 100 / p.Zoom; }
        PointF BoxCenter(Pose p) { return new PointF((float)(FW / 2 - p.X / 100 * BoxW(p)), (float)(FH / 2 + p.Y / 100 * BoxH(p))); }

        Pose FromCenter(Pose p, double cx, double cy)
        {
            p.X = (FW / 2 - cx) / BoxW(p) * 100;
            p.Y = (cy - FH / 2) / BoxH(p) * 100;
            return p;
        }

        /// <summary>Box corners and edge midpoints in frame coordinates: 0-3 corners (TL,TR,BR,BL), 4-7 edges.</summary>
        PointF[] Handles(Pose p)
        {
            PointF c = BoxCenter(p);
            double w = BoxW(p) / 2, h = BoxH(p) / 2, a = -p.Angle * Math.PI / 180, cos = Math.Cos(a), sin = Math.Sin(a);
            double[,] d = { { -w, -h }, { w, -h }, { w, h }, { -w, h }, { 0, -h }, { w, 0 }, { 0, h }, { -w, 0 } };
            PointF[] r = new PointF[8];
            for (int i = 0; i < 8; i++)
                r[i] = new PointF((float)(c.X + d[i, 0] * cos - d[i, 1] * sin), (float)(c.Y + d[i, 0] * sin + d[i, 1] * cos));
            return r;
        }

        bool InsideBox(PointF f)
        {
            PointF c = BoxCenter(pose);
            double a = pose.Angle * Math.PI / 180, dx = f.X - c.X, dy = f.Y - c.Y;
            double lx = dx * Math.Cos(a) - dy * Math.Sin(a), ly = dx * Math.Sin(a) + dy * Math.Cos(a);
            return Math.Abs(lx) <= BoxW(pose) / 2 && Math.Abs(ly) <= BoxH(pose) / 2;
        }

        Drag HitTest(Point p)
        {
            PointF[] hs = Handles(pose);
            foreach (PointF h in hs)
            {
                PointF s = ToScreen(h.X, h.Y);
                if (Math.Abs(s.X - p.X) <= Theme.D(7) && Math.Abs(s.Y - p.Y) <= Theme.D(7)) return Drag.Scale;
            }
            return InsideBox(ToFrame(p)) ? Drag.Move : Drag.Rotate;
        }

        // ---- mouse

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (e.Button != MouseButtons.Left) return;
            drag = HitTest(e.Location);
            start = pose;
            startPt = ToFrame(e.Location);
            PointF c = BoxCenter(pose);
            startDist = Math.Max(1, Math.Sqrt(Sq(startPt.X - c.X) + Sq(startPt.Y - c.Y)));
            startAngle = Math.Atan2(startPt.Y - c.Y, startPt.X - c.X);
            Capture = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (drag == Drag.None)
            {
                Drag h = HitTest(e.Location);
                Cursor = h == Drag.Move ? Cursors.SizeAll : h == Drag.Scale ? Cursors.SizeNWSE : Cursors.Hand;
                return;
            }
            PointF f = ToFrame(e.Location);
            PointF c0 = BoxCenter(start);
            Pose p = start;
            if (drag == Drag.Move)
                p = FromCenter(start, c0.X + (f.X - startPt.X), c0.Y + (f.Y - startPt.Y));
            else if (drag == Drag.Scale)
            {
                double dist = Math.Max(1, Math.Sqrt(Sq(f.X - c0.X) + Sq(f.Y - c0.Y)));
                p.Zoom = Math.Max(5, Math.Min(2000, start.Zoom * startDist / dist));
                p = FromCenter(p, c0.X, c0.Y);                       // scale about the box's own center
            }
            else
            {
                double a = Math.Atan2(f.Y - c0.Y, f.X - c0.X);
                double deg = start.Angle - (a - startAngle) * 180 / Math.PI;
                if ((ModifierKeys & Keys.Shift) != 0) deg = Math.Round(deg / 15) * 15;
                p.Angle = Math.Round(deg, 2);
            }
            p.X = Math.Round(p.X, 2);
            p.Y = Math.Round(p.Y, 2);
            p.Zoom = Math.Round(p.Zoom, 2);
            Set(p);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            drag = Drag.None;
            Capture = false;
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            PointF c = BoxCenter(pose);
            Pose p = pose;
            p.Zoom = Math.Round(Math.Max(5, Math.Min(2000, p.Zoom * (e.Delta > 0 ? 1.05 : 1 / 1.05))), 2);
            Set(FromCenter(p, c.X, c.Y));
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e) { base.OnMouseDoubleClick(e); Set(Pose.Identity); }

        static double Sq(double v) { return v * v; }

        // ---- painting

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Card);
            Theme.FillRound(g, ClientRectangle, Theme.D(6), Theme.Input, Focused ? Theme.Accent : Theme.InputBorder);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            System.Drawing.Region old = g.Clip;
            g.SetClip(new Rectangle(1, 1, Width - 2, Height - 2));

            // the source frame
            float s = ViewScale;
            PointF o = Origin;
            RectangleF frame = new RectangleF(o.X, o.Y, (float)(FW * s), (float)(FH * s));
            DrawPicture(g, frame);
            using (Pen p = new Pen(Theme.Raised)) g.DrawRectangle(p, frame.X, frame.Y, frame.Width, frame.Height);

            // the camera box, its handles and the rotation circle
            PointF[] hs = Handles(pose);
            PointF[] corners = new PointF[4];
            for (int i = 0; i < 4; i++) corners[i] = ToScreen(hs[i].X, hs[i].Y);
            PointF c = BoxCenter(pose);
            PointF cs = ToScreen(c.X, c.Y);
            float radius = (float)(Math.Sqrt(Sq(BoxW(pose)) + Sq(BoxH(pose))) / 2 * s * 1.12);
            using (Pen ring = new Pen(Color.FromArgb(90, Theme.Text)))
            {
                ring.DashStyle = DashStyle.Dash;
                g.DrawEllipse(ring, cs.X - radius, cs.Y - radius, radius * 2, radius * 2);
            }
            using (Pen shadow = new Pen(Color.FromArgb(160, 0, 0, 0), 3f)) g.DrawPolygon(shadow, corners);
            using (Pen box = new Pen(Theme.Text, 1.4f))
            {
                box.DashStyle = DashStyle.Dot;
                g.DrawPolygon(box, corners);
            }
            // a small notch marks the top edge, so rotation reads at a glance
            PointF top = ToScreen(hs[4].X, hs[4].Y);
            using (Pen notch = new Pen(Theme.Accent, 2f)) g.DrawLine(notch, top, new PointF(top.X + (cs.X - top.X) * 0.12f, top.Y + (cs.Y - top.Y) * 0.12f));
            int hsz = Theme.D(7);
            foreach (PointF h in hs)
            {
                PointF p = ToScreen(h.X, h.Y);
                RectangleF r = new RectangleF(p.X - hsz / 2f, p.Y - hsz / 2f, hsz, hsz);
                using (SolidBrush b = new SolidBrush(Theme.Text)) g.FillRectangle(b, r);
                using (Pen pen = new Pen(Theme.Bg)) g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
            }
            using (Pen cross = new Pen(Theme.Text))
            {
                g.DrawLine(cross, cs.X - Theme.D(5), cs.Y, cs.X + Theme.D(5), cs.Y);
                g.DrawLine(cross, cs.X, cs.Y - Theme.D(5), cs.X, cs.Y + Theme.D(5));
            }

            // result inset
            int iw = Math.Max(Theme.D(120), Width / 4), ih = (int)(iw * FH / FW);
            Rectangle inset = new Rectangle(Width - iw - Theme.D(10), Height - ih - Theme.D(10), iw, ih);
            using (SolidBrush b = new SolidBrush(Color.Black)) g.FillRectangle(b, inset);
            GraphicsState st = g.Save();
            g.SetClip(inset);
            g.TranslateTransform(inset.X + inset.Width / 2f, inset.Y + inset.Height / 2f);
            float k = (float)(inset.Width / FW * pose.Zoom / 100);
            g.ScaleTransform(k, k);
            g.RotateTransform((float)pose.Angle);
            g.TranslateTransform(-c.X, -c.Y);
            DrawPicture(g, new RectangleF(0, 0, (float)FW, (float)FH));
            g.Restore(st);
            using (Pen p = new Pen(Theme.InputBorder)) g.DrawRectangle(p, inset);
            TextRenderer.DrawText(g, "RESULT", Theme.Caption, new Point(inset.X + Theme.D(5), inset.Y + Theme.D(3)), Theme.Text, TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, "Drag to move · handles to scale · outside to rotate · double-click to reset", Theme.Small,
                new Point(Theme.D(10), Theme.D(8)), Theme.Faint, TextFormatFlags.NoPrefix);
            g.Clip = old;
        }

        static void DrawPicture(Graphics g, RectangleF r)
        {
            Image img = FrameRef.Image;
            if (img != null) { g.DrawImage(img, r); return; }
            // no frame to show: a neutral stand-in with thirds and a center mark
            using (LinearGradientBrush b = new LinearGradientBrush(r, Color.FromArgb(58, 64, 74), Color.FromArgb(30, 33, 38), 60f)) g.FillRectangle(b, r);
            using (Pen p = new Pen(Color.FromArgb(70, 255, 255, 255)))
            {
                for (int i = 1; i < 3; i++)
                {
                    g.DrawLine(p, r.X + r.Width * i / 3, r.Y, r.X + r.Width * i / 3, r.Bottom);
                    g.DrawLine(p, r.X, r.Y + r.Height * i / 3, r.Right, r.Y + r.Height * i / 3);
                }
                g.DrawEllipse(p, r.X + r.Width / 2 - r.Height / 8, r.Y + r.Height * 3 / 8, r.Height / 4, r.Height / 4);
            }
            using (Pen p = new Pen(Color.FromArgb(150, Theme.Accent), 2f))
                g.DrawLines(p, new PointF[] { new PointF(r.X + r.Width * 0.06f, r.Y + r.Height * 0.2f), new PointF(r.X + r.Width * 0.06f, r.Y + r.Height * 0.06f), new PointF(r.X + r.Width * 0.12f, r.Y + r.Height * 0.06f) });
        }
    }

    /// <summary>Pose editor plus exact number boxes underneath.</summary>
    internal sealed class PosePanel : Panel
    {
        public readonly PoseEditor Editor = new PoseEditor();
        readonly NumBox zoom, x, y, angle;
        readonly Label[] labels;
        readonly Btn reset;
        bool syncing;
        public event EventHandler PoseChanged;

        public PosePanel(ToolTip tips)
        {
            BackColor = Theme.Card;
            Controls.Add(Editor);
            zoom = Box(100, 5, 2000, 1);
            x = Box(0, -1000, 1000, 1);
            y = Box(0, -1000, 1000, 1);
            angle = Box(0, -3600, 3600, 1);
            labels = new Label[] { Lbl("Zoom"), Lbl("%"), Lbl("X"), Lbl("Y"), Lbl("Angle"), Lbl("°") };
            reset = new Btn("Reset", BtnKind.Ghost);
            reset.Click += delegate { Value = Pose.Identity; Raise(); };
            Controls.Add(reset);
            if (tips != null)
            {
                tips.SetToolTip(zoom, "Picture size. 100% = as is; bigger zooms in (the camera box gets smaller).");
                tips.SetToolTip(x, "Picture position, % of the frame. + is right.");
                tips.SetToolTip(y, "Picture position, % of the frame. + is up.");
                tips.SetToolTip(angle, "Picture rotation in degrees. + is clockwise.");
            }
            Editor.PoseChanged += delegate { Sync(); Raise(); };
            Sync();
        }

        NumBox Box(double v, double min, double max, double step)
        {
            NumBox n = new NumBox(v, min, max, step, 2);
            n.ValueChanged += delegate
            {
                if (syncing) return;
                Editor.Value = new Pose(zoom.Value, x.Value, y.Value, angle.Value);
                Raise();
            };
            Controls.Add(n);
            return n;
        }

        Label Lbl(string text)
        {
            Label l = new Label();
            l.AutoSize = false;
            l.UseMnemonic = false;
            l.ForeColor = Theme.Dim;
            l.TextAlign = ContentAlignment.MiddleLeft;
            l.Text = text;
            Controls.Add(l);
            return l;
        }

        public Pose Value
        {
            get { return Editor.Value; }
            set { Editor.Value = value; Sync(); }
        }

        void Raise() { if (PoseChanged != null) PoseChanged(this, EventArgs.Empty); }

        void Sync()
        {
            syncing = true;
            Pose p = Editor.Value;
            zoom.Value = p.Zoom;
            x.Value = p.X;
            y.Value = p.Y;
            angle.Value = p.Angle;
            syncing = false;
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (reset == null) return;   // still being built
            int row = Theme.D(28), fy = Height - row;
            Editor.SetBounds(0, 0, Width, Math.Max(Theme.D(60), fy - Theme.D(8)));
            int cx = 0;
            Place(labels[0], ref cx, fy, Theme.D(40)); Place(zoom, ref cx, fy, Theme.D(64)); Place(labels[1], ref cx, fy, Theme.D(18));
            cx += Theme.D(8);
            Place(labels[2], ref cx, fy, Theme.D(16)); Place(x, ref cx, fy, Theme.D(64));
            Place(labels[3], ref cx, fy, Theme.D(16)); Place(y, ref cx, fy, Theme.D(64));
            cx += Theme.D(8);
            Place(labels[4], ref cx, fy, Theme.D(40)); Place(angle, ref cx, fy, Theme.D(64)); Place(labels[5], ref cx, fy, Theme.D(14));
            reset.SetBounds(Width - Theme.D(64), fy, Theme.D(64), row);
        }

        static void Place(Control c, ref int cx, int y, int w)
        {
            c.SetBounds(cx, y, w, Theme.D(28));
            cx += w + Theme.D(4);
        }
    }

    /// <summary>Keyframe timeline for the Pan/Crop effect: note start on the left, note end on the right.</summary>
    internal sealed class KeyStrip : Painted
    {
        public EffectSpec Spec;
        int selected, dragging = -1;
        public event EventHandler SelectedChanged, KeysChanged;

        public int Selected
        {
            get { return selected; }
            set { selected = Math.Max(0, Math.Min(Spec.Keys.Count - 1, value)); Invalidate(); if (SelectedChanged != null) SelectedChanged(this, EventArgs.Empty); }
        }

        Rectangle Track { get { int pad = Theme.D(18); return new Rectangle(pad, Theme.D(10), Math.Max(1, Width - pad * 2), Theme.D(22)); } }
        float XOf(double t) { Rectangle r = Track; return r.X + (float)t * r.Width; }
        double TOf(int x) { Rectangle r = Track; return Math.Max(0, Math.Min(1, (x - r.X) / (double)r.Width)); }

        int KeyAt(int x)
        {
            for (int i = 0; i < Spec.Keys.Count; i++) if (Math.Abs(XOf(Spec.Keys[i].T) - x) <= Theme.D(7)) return i;
            return -1;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (e.Button != MouseButtons.Left) return;
            int i = KeyAt(e.X);
            if (i < 0)
            {
                double t = Math.Round(TOf(e.X), 3);
                PoseKey k = new PoseKey(t, Spec.PoseAt(t));
                Spec.Keys.Add(k);
                Spec.SortKeys();
                i = Spec.Keys.IndexOf(k);
                if (KeysChanged != null) KeysChanged(this, EventArgs.Empty);
            }
            dragging = i;
            Selected = i;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragging < 0 || (e.Button & MouseButtons.Left) == 0) return;
            PoseKey k = Spec.Keys[dragging];
            k.T = Math.Round(TOf(e.X), 3);
            Spec.SortKeys();
            dragging = Spec.Keys.IndexOf(k);
            selected = dragging;
            Invalidate();
            if (KeysChanged != null) KeysChanged(this, EventArgs.Empty);
        }

        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); dragging = -1; }

        public void DeleteSelected()
        {
            if (Spec.Keys.Count <= 1) return;
            Spec.Keys.RemoveAt(selected);
            Selected = Math.Min(selected, Spec.Keys.Count - 1);
            if (KeysChanged != null) KeysChanged(this, EventArgs.Empty);
        }

        protected override bool IsInputKey(Keys keyData) { return keyData == Keys.Left || keyData == Keys.Right || base.IsInputKey(keyData); }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Delete || e.KeyCode == Keys.Back) DeleteSelected();
            else if (e.KeyCode == Keys.Left) Selected = selected - 1;
            else if (e.KeyCode == Keys.Right) Selected = selected + 1;
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Behind);
            Rectangle r = Track;
            Theme.FillRound(g, new Rectangle(r.X - Theme.D(10), r.Y, r.Width + Theme.D(20), r.Height), Theme.D(4), Theme.Input, Focused ? Theme.Accent : Theme.InputBorder);
            int mid = r.Y + r.Height / 2;
            using (Pen p = new Pen(Theme.Raised)) g.DrawLine(p, r.X, mid, r.Right, mid);
            using (Pen p = new Pen(Theme.Border))
                for (int i = 0; i <= 4; i++) g.DrawLine(p, XOf(i / 4.0), r.Y + Theme.D(4), XOf(i / 4.0), r.Bottom - Theme.D(4));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            for (int i = 0; i < Spec.Keys.Count; i++)
            {
                float x = XOf(Spec.Keys[i].T), d = Theme.D(i == selected ? 7 : 6);
                PointF[] diamond = { new PointF(x, mid - d), new PointF(x + d, mid), new PointF(x, mid + d), new PointF(x - d, mid) };
                using (SolidBrush b = new SolidBrush(i == selected ? Theme.Text : Theme.Accent)) g.FillPolygon(b, diamond);
                using (Pen p = new Pen(Theme.Bg)) g.DrawPolygon(p, diamond);
            }
            int ly = r.Bottom + Theme.D(4);
            TextRenderer.DrawText(g, "note start", Theme.Small, new Point(r.X - Theme.D(10), ly), Theme.Faint, TextFormatFlags.NoPrefix);
            string end = "note end";
            Size es = TextRenderer.MeasureText(end, Theme.Small);
            TextRenderer.DrawText(g, end, Theme.Small, new Point(r.Right + Theme.D(10) - es.Width, ly), Theme.Faint, TextFormatFlags.NoPrefix);
            if (selected >= 0 && selected < Spec.Keys.Count)
            {
                // where the selected keyframe sits, kept clear of the start / end labels
                string at = Math.Round(Spec.Keys[selected].T * 100) + "%";
                Size s = TextRenderer.MeasureText(at, Theme.Small);
                int sx = (int)XOf(Spec.Keys[selected].T) - s.Width / 2;
                Size ss = TextRenderer.MeasureText("note start", Theme.Small);
                sx = Math.Max(r.X - Theme.D(10) + ss.Width + Theme.D(8), Math.Min(r.Right + Theme.D(10) - es.Width - Theme.D(8) - s.Width, sx));
                TextRenderer.DrawText(g, at, Theme.Small, new Point(sx, ly), Theme.Text, TextFormatFlags.NoPrefix);
            }
        }
    }

    /// <summary>The Pan/Crop effect's editor: keyframe strip + pose editor.</summary>
    internal sealed class MotionDialog : Form
    {
        readonly EffectSpec work;
        readonly PosePanel pose;
        readonly KeyStrip strip;
        readonly Btn del, ok, cancel;
        readonly Label hint;

        MotionDialog(EffectSpec spec, ToolTip tips)
        {
            work = spec.Clone();
            if (work.Keys.Count == 0) work.Keys.Add(new PoseKey(0, Pose.Identity));
            Text = "Pan/Crop keyframes";
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ShowIcon = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Theme.Card;
            ForeColor = Theme.Text;
            Font = Theme.Base;
            KeyPreview = true;
            MinimumSize = SizeFromClientSize(new Size(Theme.D(560), Theme.D(440)));
            ClientSize = new Size(Theme.D(820), Theme.D(620));

            strip = new KeyStrip();
            strip.Spec = work;
            Controls.Add(strip);
            pose = new PosePanel(tips);
            Controls.Add(pose);
            hint = new Label();
            hint.AutoSize = false;
            hint.ForeColor = Theme.Faint;
            hint.Font = Theme.Small;
            hint.Text = "Click the strip to add a keyframe, drag to move it, Delete to remove it. Keyframes stack on top of Zoom / Rotate / Move.";
            Controls.Add(hint);
            del = new Btn("Delete keyframe", BtnKind.Secondary);
            del.Click += delegate { strip.DeleteSelected(); };
            Controls.Add(del);
            ok = new Btn("Done", BtnKind.Primary);
            ok.DialogResult = DialogResult.OK;
            Controls.Add(ok);
            cancel = new Btn("Cancel", BtnKind.Secondary);
            cancel.DialogResult = DialogResult.Cancel;
            Controls.Add(cancel);
            AcceptButton = ok;
            CancelButton = cancel;

            strip.SelectedChanged += delegate { pose.Value = work.Keys[strip.Selected].P; del.Enabled = work.Keys.Count > 1; };
            strip.KeysChanged += delegate { del.Enabled = work.Keys.Count > 1; };
            pose.PoseChanged += delegate { work.Keys[strip.Selected].P = pose.Value; strip.Invalidate(); };
            strip.Selected = 0;
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Native.DarkTitleBar(Handle); }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (cancel == null) return;   // still being built (cancel is created last)
            int m = Theme.D(16), W = ClientSize.Width, H = ClientSize.Height;
            strip.SetBounds(m, m, W - m * 2, Theme.D(54));
            hint.SetBounds(m, strip.Bottom, W - m * 2, Theme.D(20));
            int foot = H - m - Theme.D(34);
            pose.SetBounds(m, hint.Bottom + Theme.D(6), W - m * 2, Math.Max(Theme.D(120), foot - hint.Bottom - Theme.D(18)));
            ok.SetBounds(W - m - Theme.D(110), foot, Theme.D(110), Theme.D(34));
            cancel.SetBounds(ok.Left - Theme.D(8) - Theme.D(92), foot, Theme.D(92), Theme.D(34));
            del.SetBounds(m, foot, Theme.D(140), Theme.D(34));
        }

        public static bool Edit(IWin32Window owner, EffectSpec spec, ToolTip tips)
        {
            using (MotionDialog d = new MotionDialog(spec, tips))
            {
                if (d.ShowDialog(owner) != DialogResult.OK) return false;
                d.work.SortKeys();
                spec.Keys = d.work.Keys;
                return true;
            }
        }
    }

    /// <summary>Flat tabs with an underline, VEGAS-style.</summary>
    internal sealed class TabStrip : Painted
    {
        readonly string[] items;
        int sel, hot = -1;
        public event EventHandler Changed;

        public TabStrip(params string[] items) { this.items = items; TabStop = false; }

        public int Value
        {
            get { return sel; }
            set
            {
                int v = Math.Max(0, Math.Min(items.Length - 1, value));
                if (v == sel) return;
                sel = v;
                Invalidate();
                if (Changed != null) Changed(this, EventArgs.Empty);
            }
        }

        Rectangle Slot(int i)
        {
            int x = Theme.D(6);
            for (int k = 0; k < items.Length; k++)
            {
                int w = TextRenderer.MeasureText(items[k], Theme.Semi).Width + Theme.D(26);
                if (k == i) return new Rectangle(x, 0, w, Height);
                x += w;
            }
            return Rectangle.Empty;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = -1;
            for (int i = 0; i < items.Length; i++) if (Slot(i).Contains(e.Location)) h = i;
            if (h != hot) { hot = h; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { hot = -1; base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            for (int i = 0; i < items.Length; i++) if (Slot(i).Contains(e.Location) && e.Button == MouseButtons.Left) Value = i;
            base.OnMouseDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Behind);
            using (Pen p = new Pen(Theme.Border)) g.DrawLine(p, 0, Height - 1, Width, Height - 1);
            for (int i = 0; i < items.Length; i++)
            {
                Rectangle r = Slot(i);
                Color fg = i == sel ? Theme.Text : i == hot ? Theme.Text : Theme.Dim;
                TextRenderer.DrawText(g, items[i], i == sel ? Theme.Semi : Theme.Base, r, fg,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
                if (i == sel)
                    using (SolidBrush b = new SolidBrush(Theme.Accent))
                        g.FillRectangle(b, r.X + Theme.D(10), Height - Theme.D(3), r.Width - Theme.D(20), Theme.D(2));
            }
        }
    }

    /// <summary>Checklist of FL channels: "[x] Ch 3  Lead   steps".</summary>
    internal sealed class ChannelList : ListBox
    {
        List<Part> parts = new List<Part>();
        bool[] picked = new bool[0];
        int hot = -1;
        public event EventHandler PickedChanged;
        public Converter<Part, string> SourceTag;   // "subclip", "no clip" or ""

        public ChannelList()
        {
            DrawMode = DrawMode.OwnerDrawFixed;
            BorderStyle = BorderStyle.None;
            BackColor = Theme.Card;
            ForeColor = Theme.Text;
            Font = Theme.Base;
            IntegralHeight = false;
            ItemHeight = Math.Min(255, Theme.D(30));
            SelectionMode = System.Windows.Forms.SelectionMode.None;
            Cursor = Cursors.Hand;
        }

        public void SetParts(List<Part> parts, bool[] picked)
        {
            this.parts = parts;
            this.picked = picked;
            BeginUpdate();
            Items.Clear();
            foreach (Part p in parts) Items.Add(p.Label);
            EndUpdate();
            Invalidate();
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Native.DarkScrollbars(this); }

        protected override void OnResize(EventArgs e) { base.OnResize(e); Invalidate(); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            int i = IndexFromPoint(e.Location);
            if (e.Button != MouseButtons.Left || i < 0 || i >= picked.Length) return;
            picked[i] = !picked[i];
            Invalidate(GetItemRectangle(i));
            if (PickedChanged != null) PickedChanged(this, EventArgs.Empty);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int i = IndexFromPoint(e.Location);
            if (i >= 0 && !GetItemRectangle(i).Contains(e.Location)) i = -1;
            if (i == hot) return;
            if (hot >= 0 && hot < Items.Count) Invalidate(GetItemRectangle(hot));
            hot = i;
            if (hot >= 0) Invalidate(GetItemRectangle(hot));
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hot >= 0 && hot < Items.Count) Invalidate(GetItemRectangle(hot));
            hot = -1;
        }

        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            Graphics g = e.Graphics;
            Rectangle r = e.Bounds;
            using (SolidBrush b = new SolidBrush(BackColor)) g.FillRectangle(b, r);
            if (e.Index < 0 || e.Index >= parts.Count) return;
            bool on = picked[e.Index];
            Rectangle inner = new Rectangle(r.X + Theme.D(4), r.Y + Theme.D(1), r.Width - Theme.D(8), r.Height - Theme.D(2));
            if (on) Theme.FillRound(g, inner, Theme.D(4), Theme.AccentSoft, Theme.AccentSoft);
            else if (e.Index == hot) Theme.FillRound(g, inner, Theme.D(4), Theme.Row, Theme.Row);

            // black-and-white checkbox
            int box = Theme.D(14);
            Rectangle cb = new Rectangle(inner.X + Theme.D(9), inner.Y + (inner.Height - box) / 2, box, box);
            if (on)
            {
                Theme.FillRound(g, cb, Theme.D(3), Theme.Text, Theme.Text);
                float x = cb.X, y = cb.Y, w = cb.Width;
                Theme.Lines(g, Theme.Bg, 1.8f * Theme.S, new PointF(x + w * 0.24f, y + w * 0.52f), new PointF(x + w * 0.43f, y + w * 0.70f), new PointF(x + w * 0.77f, y + w * 0.31f));
            }
            else Theme.FillRound(g, cb, Theme.D(3), Theme.Input, Theme.InputBorder);

            Part part = parts[e.Index];
            const TextFormatFlags line = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
            int badgeW = TextRenderer.MeasureText("CH 188", Theme.Caption).Width;
            Rectangle badge = new Rectangle(cb.Right + Theme.D(9), inner.Y, badgeW, inner.Height);
            TextRenderer.DrawText(g, part.ChannelText.ToUpperInvariant(), Theme.Caption, badge, on ? Theme.Accent : Theme.Faint, line | TextFormatFlags.Left);

            // right side: what kind of notes it has, and where its clip comes from
            int right = inner.Right - Theme.D(8);
            string tag = SourceTag != null ? SourceTag(part) : "";
            if (tag.Length > 0)
            {
                Size ts = TextRenderer.MeasureText(tag, Theme.Small);
                Rectangle tr = new Rectangle(right - ts.Width, inner.Y, ts.Width, inner.Height);
                TextRenderer.DrawText(g, tag, Theme.Small, tr, tag == "subclip" ? Theme.Accent : on ? Theme.Danger : Theme.Faint, line | TextFormatFlags.Right);
                right = tr.Left - Theme.D(8);
            }
            string extra = part.Extra;
            if (extra.Length > 0)
            {
                Size es = TextRenderer.MeasureText(extra, Theme.Small);
                int ew = Math.Min(es.Width, Math.Max(0, (right - badge.Right) / 2));
                Rectangle er = new Rectangle(right - ew, inner.Y, ew, inner.Height);
                TextRenderer.DrawText(g, extra, Theme.Small, er, on ? Theme.Dim : Theme.Faint, line | TextFormatFlags.Right);
                right = er.Left - Theme.D(8);
            }
            Rectangle nr = new Rectangle(badge.Right + Theme.D(6), inner.Y, Math.Max(1, right - badge.Right - Theme.D(6)), inner.Height);
            TextRenderer.DrawText(g, part.Name, Theme.Base, nr, on ? Theme.Text : Theme.Dim, line | TextFormatFlags.Left);
        }
    }

    /// <summary>One added effect with only the settings it needs.</summary>
    internal sealed class EffectRow : Panel
    {
        static readonly string[] Curves = { "Linear", "Ease out", "Ease in", "Smooth" };
        static readonly string[] Patterns = { "Notes 2, 4, 6\u2026", "Notes 1, 3, 5\u2026", "Every note" };
        static readonly string[] Axes = { "Horizontal", "Vertical", "Both", "Clockwise", "Counterclockwise" };

        public readonly EffectSpec Spec;
        public event EventHandler RemoveRequested;
        readonly IconBtn remove;
        readonly ToolTip tips;
        int cursor;

        static int D(double v) { return Theme.D(v); }

        public EffectRow(EffectSpec spec, ToolTip tips)
        {
            Spec = spec;
            this.tips = tips;
            BackColor = Theme.Row;
            DoubleBuffered = true;
            Height = D(36);

            Label name = MakeLabel(FxInfo.Name(spec.Type), Theme.Semi, Theme.Text, D(92));
            name.Left = D(12);
            if (tips != null) tips.SetToolTip(name, FxInfo.Hint(spec.Type));
            cursor = D(108);

            switch (spec.Type)
            {
                case Fx.Zoom: Range("%", 1, 1000, 5); break;
                case Fx.Rotate: Range("\u00B0", -3600, 3600, 5); break;
                case Fx.Hue: Range("\u00B0", 0, 360, 10); break;
                case Fx.Saturation: Range("%", 0, 400, 10); break;
                case Fx.Contrast: Range("", -100, 100, 5); break;
                case Fx.Blur: Range("", 0, 100, 1); break;
                case Fx.Move:
                    Say("X");
                    Num(spec.A, -200, 200, 5, delegate (double v) { Spec.A = v; });
                    Say("\u2192");
                    Num(spec.B, -200, 200, 5, delegate (double v) { Spec.B = v; });
                    cursor += D(8);
                    Say("Y");
                    Num(spec.C, -200, 200, 5, delegate (double v) { Spec.C = v; });
                    Say("\u2192");
                    Num(spec.D, -200, 200, 5, delegate (double v) { Spec.D = v; });
                    CurveChoice();
                    break;
                case Fx.Fade:
                    Say("In");
                    Num(spec.A, 0, 100, 5, delegate (double v) { Spec.A = v; });
                    Say("%");
                    cursor += D(8);
                    Say("Out");
                    Num(spec.B, 0, 100, 5, delegate (double v) { Spec.B = v; });
                    Say("% of the clip");
                    break;
                case Fx.Flash:
                    Say("Amount");
                    Num(spec.A, 0, 100, 5, delegate (double v) { Spec.A = v; });
                    Say("%");
                    cursor += D(8);
                    Say("Fades over");
                    Num(spec.B, 1, 100, 5, delegate (double v) { Spec.B = v; });
                    Say("% of the note");
                    break;
                case Fx.Flip:
                    {
                        Dropdown axis = Choice(Axes, spec.Axis, D(160), delegate (int v) { Spec.Axis = v; });
                        int x = cursor;
                        Dropdown pattern = Choice(Patterns, spec.Pattern, D(142), delegate (int v) { Spec.Pattern = v; });
                        cursor = x;
                        Label cycle = MakeLabel("4-step cycle, one step per note", Theme.Base, Theme.Dim, D(240));
                        EventHandler sync = delegate
                        {
                            pattern.Visible = Spec.Axis < 3;
                            cycle.Visible = Spec.Axis >= 3;
                        };
                        axis.Changed += sync;
                        sync(null, EventArgs.Empty);
                    }
                    break;
                case Fx.Negative:
                case Fx.Greyscale:
                case Fx.Reverse:
                    Choice(Patterns, spec.Pattern, D(142), delegate (int v) { Spec.Pattern = v; });
                    break;
                case Fx.HueSteps:
                    Num(spec.A, 2, 24, 1, delegate (double v) { Spec.A = v; });
                    Say("steps around the color wheel, one per note");
                    break;
                case Fx.PanCrop:
                    {
                        Btn edit = new Btn("Edit keyframes…", BtnKind.Secondary);
                        edit.SetBounds(cursor, (Height - D(28)) / 2, D(130), D(28));
                        Controls.Add(edit);
                        cursor += D(138);
                        Label count = MakeLabel(KeyCount(), Theme.Base, Theme.Dim, D(110));
                        cursor += D(114);
                        edit.Click += delegate
                        {
                            if (MotionDialog.Edit(FindForm(), Spec, tips)) count.Text = KeyCount();
                        };
                        CurveChoice();
                    }
                    break;
            }

            remove = new IconBtn();
            remove.Size = new Size(D(26), D(26));
            remove.Click += delegate { if (RemoveRequested != null) RemoveRequested(this, EventArgs.Empty); };
            if (tips != null) tips.SetToolTip(remove, "Remove");
            Controls.Add(remove);
        }

        string KeyCount() { return Spec.Keys.Count + (Spec.Keys.Count == 1 ? " keyframe" : " keyframes"); }

        Label MakeLabel(string text, Font font, Color color, int width)
        {
            Label l = new Label();
            l.AutoSize = false;
            l.UseMnemonic = false;
            l.Font = font;
            l.ForeColor = color;
            l.Text = text;
            l.TextAlign = ContentAlignment.MiddleLeft;
            l.SetBounds(cursor, 0, width, Height);
            Controls.Add(l);
            return l;
        }

        void Say(string text)
        {
            int w = TextRenderer.MeasureText(text, Theme.Base, new Size(1000, 100), TextFormatFlags.NoPadding).Width + D(4);
            MakeLabel(text, Theme.Base, Theme.Dim, w);
            cursor += w + D(4);
        }

        void Num(double v, double min, double max, double step, Action<double> set)
        {
            NumBox n = new NumBox(v, min, max, step, 0);
            n.SetBounds(cursor, (Height - D(28)) / 2, D(56), D(28));
            n.ValueChanged += delegate { set(n.Value); };
            Controls.Add(n);
            cursor += D(56) + D(6);
        }

        Dropdown Choice(string[] items, int value, int width, Action<int> set)
        {
            Dropdown d = new Dropdown(items);
            d.SetValueSilently(value);
            d.SetBounds(cursor, (Height - D(28)) / 2, width, D(28));
            d.Changed += delegate { set(d.Value); };
            Controls.Add(d);
            cursor += width + D(8);
            return d;
        }

        void Range(string unit, double min, double max, double step)
        {
            Num(Spec.A, min, max, step, delegate (double v) { Spec.A = v; });
            Say("\u2192");
            Num(Spec.B, min, max, step, delegate (double v) { Spec.B = v; });
            if (unit.Length > 0) Say(unit);
            CurveChoice();
        }

        void CurveChoice()
        {
            cursor += D(6);
            Choice(Curves, Spec.Curve, D(98), delegate (int v) { Spec.Curve = v; });
            if (tips != null) tips.SetToolTip(Controls[Controls.Count - 1], "How the change is paced across the note.");
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (remove != null) remove.Location = new Point(Width - D(34), (Height - remove.Height) / 2);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent != null ? Parent.BackColor : Theme.Card);
            Theme.FillRound(e.Graphics, ClientRectangle, D(8), Theme.Row, Theme.Row);
        }
    }
}
