using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace LaneReader
{
    /// <summary>One note in the falling timeline, already assigned to a lane.</summary>
    public struct Note
    {
        /// <summary>Song-clock time in seconds when this note reaches the judgement line.</summary>
        public double HitTime;

        /// <summary>0..3.</summary>
        public int Lane;

        /// <summary>Chart tile index (scrFloor.seqID).</summary>
        public int SeqId;

        /// <summary>+1 if the chart turns clockwise out of this tile, -1 otherwise.</summary>
        public int Direction;

        /// <summary>The live scrFloor, so the renderer can read the game's own judgement.</summary>
        public object Floor;

        /// <summary>
        /// True when this note came from a midspin tile. A midspin is carried by a pair of tiles
        /// at one instant, so the duplicate is dropped - and only midspins are deduplicated,
        /// because two genuinely simultaneous taps elsewhere are a different case.
        /// </summary>
        public bool MidSpin;

        /// <summary>
        /// How long this tile lasts, in beats, taken from the chart's own geometry: the planet's
        /// turn between this tile and the next, over 180 degrees. A normal tile is one beat, a
        /// twirl two, a shallower turn less than one.
        ///
        /// This is ADOFAI's own notion of a beat, so it needs no BPM at all - which is what lets
        /// the beat lines follow a chart through its speed changes instead of being pinned to one
        /// tempo. Verified against a real chart: at 170 BPM a 180-degree tile lasts 352.94 ms,
        /// exactly one beat, while a 300-degree tile lasts 1.667 beats.
        /// </summary>
        public double Beats;

        /// <summary>
        /// How long this tile is held, in seconds. -1 on every ordinary tile; 0 or more on a hold.
        /// It is the WHOLE gap to the next tile, because that is what the game's own hold mesh spans
        /// (scrHoldRenderer.tailFloor = startFloor.nextfloor) - the key is down until the next tile
        /// arrives, so the bar has to reach it. See Build().
        /// </summary>
        public double HoldSeconds;

        /// <summary>
        /// scrFloor.holdLength as the game reports it: the hold's length in beats of extra
        /// rotation, or -1 for a tile that is not a hold at all. A hold event with duration n puts
        /// n * 2 beats on top of the tile's own length (scrLevelMaker.CalculateFloorEntryTimes adds
        /// holdLength * 2 * timeBetweenAngles), which is why this is the game's own value rather
        /// than anything derived here.
        /// </summary>
        public double HoldBeats;

        /// <summary>
        /// The angle the beat grid measures this note's turn from, when that is not the angle of
        /// <see cref="Floor"/>. Only a folded midspin sets it: the press is the flagged floor in
        /// front of the midspin tile, but the rotation the game spends there belongs to the midspin
        /// tile behind it, whose entry angle is half a turn further round - so measuring from the
        /// flagged floor would read the pair's 360-degree spin as 180. NaN means "read it off the
        /// floor"; set explicitly in Build(), because a struct cannot initialise a field.
        /// </summary>
        public double GridAngle;
    }

    /// <summary>One beat line: when it crosses the judgement line, and which beat it is.</summary>
    public struct BeatLine
    {
        public double Time;

        /// <summary>Beat number from the chart's start, used to pick the bar lines.</summary>
        public int Beat;
    }

    /// <summary>The game's own judgement value for a tile (scrFloor.grade / HitMargin).</summary>
    public enum Grade
    {
        None = -1,
        TooEarly = 0,
        VeryEarly = 1,
        EarlyPerfect = 2,
        Perfect = 3,
        LatePerfect = 4,
        VeryLate = 5,
        TooLate = 6,
        Multipress = 7,
        FailMiss = 8,
        FailOverload = 9,
        Auto = 10,
        OverPress = 11,
    }

    /// <summary>
    /// Reads the chart the game has already built in memory.
    ///
    /// The previous attempt at this failed because it read
    /// <c>scrLevelMaker.instance</c> before the level existed and then never retried
    /// meaningfully. This version treats a null singleton as "not ready yet" and simply
    /// keeps polling, then rebuilds whenever the floor list identity changes.
    /// </summary>
    public sealed class ChartReader
    {
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        // Resolved reflection handles.
        private PropertyInfo _lmInstance;
        private FieldInfo _lmListFloors;
        private FieldInfo _lmBpm;
        private FieldInfo _floorSeqId;
        private FieldInfo _floorEntryTime;
        private FieldInfo _floorEntryAngle;
        private FieldInfo _floorExitAngle;
        private FieldInfo _floorSpeed;
        private FieldInfo _floorGrade;
        private FieldInfo _floorMidSpin;
        private FieldInfo _floorIsFake;
        private FieldInfo _floorHoldLength;
        private FieldInfo _floorTapsNeeded;
        private PropertyInfo _conductorInstance;
        private PropertyInfo _conductorSongPosProp;
        private FieldInfo _conductorSongPosField;
        private FieldInfo _conductorDspTimeSong;
        private FieldInfo _conductorHasSongStarted;

        // "True BPM" (TBPM): what JipperResourcePack shows as TBPM is
        // scrConductor.bpm * song.pitch * planet speed, and that is the tempo the chart is really
        // running at - it changes whenever the chart speeds up or slows down.
        private FieldInfo _conductorBpm;
        private FieldInfo _conductorSong;
        private PropertyInfo _songPitch;
        private FieldInfo _ctrlSpeed;
        private FieldInfo _ctrlPlayerOne;
        private FieldInfo _planetSystem;
        private PropertyInfo _planetSystemSpeedProp;
        private FieldInfo _planetSystemSpeedField;

        private PropertyInfo _ctrlInstance;
        private PropertyInfo _ctrlPaused;
        private FieldInfo _ctrlCurrentState;
        private FieldInfo _ctrlCurrentSeqId;

        // Level editor: playMode distinguishes testing from building.
        private FieldInfo _editorInstance;
        private PropertyInfo _editorPlayMode;
        private PropertyInfo _editorPausedInPlay;

        /// <summary>
        /// States in which the run is over and the overlay must stop moving. Without this the
        /// notes keep falling while the death screen is up, because the audio clock runs on.
        /// </summary>
        private static readonly string[] FrozenStates = { "Fail", "Fail2", "Won" };

        /// <summary>States before the music starts. Notes must not be drawn here.</summary>
        private static readonly string[] PreSongStates = { "None", "Start", "Checkpoint", "Countdown" };

        /// <summary>
        /// States in which a level is loaded and the overlay's static frame belongs on screen.
        ///
        /// "Countdown" covers both the 3-2-1 before a checkpoint resume and the "press any key
        /// to start" prompt a fresh level opens with. The frame is wanted on both; the notes are
        /// not, and they are gated separately - see <see cref="TimelineLive"/>.
        /// </summary>
        private static readonly string[] StandingStates = { "Start", "Countdown", "PlayerControl", "Checkpoint" };

        private bool _reflectionReady;
        private bool _reflectionFailed;
        private string _diagnostic = "not initialised";

        // Current chart.
        private readonly List<Note> _notes = new List<Note>();

        /// <summary>Every beat boundary in the chart, in time order; see <see cref="Note.Beats"/>.</summary>
        private readonly List<BeatLine> _beatLines = new List<BeatLine>();
        private object _lastFloorList;
        private int _lastFloorCount = -1;
        private double _firstHitTime;
        private double _lastHitTime;

        // ---- render clock -----------------------------------------------------------
        // Kept separate from the raw audio clock so pausing/death can freeze the overlay
        // without touching the value the notes are positioned against during normal play.
        private readonly HashSet<int> _cleared = new HashSet<int>();

        /// <summary>
        /// Bumped every time the note list is rebuilt. Callers use it to tell "same chart, same
        /// run" from "the chart was rebuilt", which is the only reliable way to know a restart
        /// happened: the note count and timings can be identical across a retry.
        /// </summary>
        private int _generation;
        private double _frozenAt = double.NaN;
        private double _previousRaw = double.NaN;

        /// <summary>
        /// True once the game's clock has been seen to move since this chart was set up. A clock
        /// that never moves is not a timeline the notes can be positioned against - see
        /// <see cref="TimelineLive"/>.
        /// </summary>
        private bool _clockMoved;

        private double _displayTime;
        private bool _displayTimeValid;

        public IList<Note> Notes { get { return _notes; } }

        /// <summary>Beat boundaries for the whole chart, in time order.</summary>
        public IList<BeatLine> BeatLines { get { return _beatLines; } }
        public int Count { get { return _notes.Count; } }
        public double FirstHitTime { get { return _firstHitTime; } }
        public double LastHitTime { get { return _lastHitTime; } }
        public string Diagnostic { get { return _diagnostic; } }
        public bool Ready { get { return _notes.Count > 0; } }

        /// <summary>Song clock in seconds, read straight from the game's own conductor.</summary>
        public double SongPosition()
        {
            object conductor = ReadStatic(_conductorInstance);
            if (conductor == null)
                return 0.0;

            // Prefer the public property; fall back to the backing field. The field is named
            // _songposition_minusi with a leading underscore, so an exact-name lookup of
            // "songposition_minusi" silently finds nothing and would freeze the timeline at 0.
            double v = ReadDouble(conductor, _conductorSongPosProp);
            if (double.IsNaN(v))
                v = ReadDouble(conductor, _conductorSongPosField);
            if (double.IsNaN(v))
                v = ReadDouble(conductor, _conductorDspTimeSong);
            return double.IsNaN(v) ? 0.0 : v;
        }

        private static double ReadDouble(object target, MemberInfo member)
        {
            if (target == null || member == null)
                return double.NaN;
            try
            {
                var p = member as PropertyInfo;
                if (p != null) return ToDouble(p.GetValue(target, null));
                var f = member as FieldInfo;
                if (f != null) return ToDouble(f.GetValue(target));
            }
            catch
            {
                // fall through to the next source
            }
            return double.NaN;
        }

        public void Reset()
        {
            _notes.Clear();
            _lastFloorList = null;
            _lastFloorCount = -1;
            _firstHitTime = 0.0;
            _lastHitTime = 0.0;
            _clockMoved = false;
        }

        // ------------------------------------------------------------ reflection setup

        private void EnsureReflection()
        {
            if (_reflectionReady || _reflectionFailed)
                return;

            try
            {
                Assembly asm = null;
                foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (a.GetName().Name == "Assembly-CSharp") { asm = a; break; }
                }
                if (asm == null)
                {
                    _diagnostic = "Assembly-CSharp not loaded";
                    return;
                }

                Type lm = asm.GetType("scrLevelMaker", false);
                Type floor = asm.GetType("scrFloor", false);
                Type cond = asm.GetType("scrConductor", false);
                Type ctrl = asm.GetType("scrController", false);
                if (lm == null || floor == null || cond == null || ctrl == null)
                {
                    _diagnostic = string.Format(
                        "missing types: levelMaker={0} floor={1} conductor={2} controller={3}",
                        lm != null, floor != null, cond != null, ctrl != null);
                    _reflectionFailed = true;
                    // Loud on purpose: with no reader there is no chart, and with no chart every
                    // path below returns early - so without this line a broken reflection setup
                    // looks exactly like "the mod does nothing". Once only; it never succeeds after.
                    Plugin.Log("chart reader disabled: " + _diagnostic);
                    return;
                }

                _lmInstance = lm.GetProperty("instance", Any);
                _lmListFloors = lm.GetField("listFloors", Any);
                _lmBpm = lm.GetField("bpm_forUnityUseOnly", Any);

                _floorSeqId = floor.GetField("seqID", Any);
                _floorEntryTime = floor.GetField("entryTime", Any);
                _floorEntryAngle = floor.GetField("entryangle", Any);
                _floorExitAngle = floor.GetField("exitangle", Any);
                _floorSpeed = floor.GetField("speed", Any);
                // The game's own judgement for each tile. Reading this is how the overlay knows
                // a note has been struck, how well, and whether it was missed.
                _floorGrade = floor.GetField("grade", Any);
                // Midspins and fake tiles are not separate presses: the planet passes through
                // them without the player tapping. Drawing them as notes produced phantom hits
                // in the middle of every stream.
                _floorMidSpin = floor.GetField("midSpin", Any);
                _floorIsFake = floor.GetField("isFake", Any);
                // Holds: scrFloor.holdLength is the length of the hold in beats. It is zero on every
                // ordinary tile, so a chart without holds is completely unaffected.
                _floorHoldLength = floor.GetField("holdLength", Any);
                // Multipress tiles ("多次按键方块"): the tile needs more than one press. The game
                // stores the required count in scrFloor.tapsNeeded (1 on ordinary tiles). Reading
                // it lets one note per tap be drawn instead of collapsing the tile into one.
                _floorTapsNeeded = floor.GetField("tapsNeeded", Any);

                _conductorInstance = cond.GetProperty("instance", Any);
                _conductorSongPosProp = cond.GetProperty("songposition_minusi", Any);
                _conductorSongPosField = cond.GetField("_songposition_minusi", Any);
                _conductorDspTimeSong = cond.GetField("dspTimeSong", Any);
                _conductorHasSongStarted = cond.GetField("hasSongStarted", Any);
                // TBPM ingredients, as JipperResourcePack's BPM display reads them.
                _conductorBpm = cond.GetField("bpm", Any);
                _conductorSong = cond.GetField("song", Any);

                _ctrlInstance = ctrl.GetProperty("instance", Any);
                _ctrlPaused = ctrl.GetProperty("paused", Any);
                _ctrlCurrentState = ctrl.GetField("currentState", Any);
                _ctrlCurrentSeqId = ctrl.GetField("currentSeqID", Any);
                // Planet speed: the controller owns it directly on older builds, and on newer ones
                // it lives under playerOne.planetarySystem. Both are tried; see PlanetSpeed().
                _ctrlSpeed = ctrl.GetField("speed", Any);
                _ctrlPlayerOne = ctrl.GetField("playerOne", Any);
                if (_ctrlPlayerOne != null)
                {
                    Type one = _ctrlPlayerOne.FieldType;
                    _planetSystem = one.GetField("planetarySystem", Any);
                    if (_planetSystem != null)
                    {
                        Type ps = _planetSystem.FieldType;
                        _planetSystemSpeedProp = ps.GetProperty("speed", Any);
                        _planetSystemSpeedField = ps.GetField("speed", Any);
                    }
                }

                Type editor = asm.GetType("scnEditor", false);
                if (editor != null)
                {
                    _editorInstance = editor.GetField("instance", Any);
                    _editorPlayMode = editor.GetProperty("playMode", Any);
                    _editorPausedInPlay = editor.GetProperty("pausedInPlayMode", Any);
                }

                if (_lmInstance == null || _lmListFloors == null)
                {
                    _diagnostic = "core members missing (instance / listFloors)";
                    _reflectionFailed = true;
                    Plugin.Log("chart reader disabled: " + _diagnostic);
                    return;
                }
                if (_ctrlPaused == null)
                    _diagnostic = "warning: scrController.paused not found; pause gating disabled";

                _reflectionReady = true;
                _diagnostic = "reflection ready";
            }
            catch (Exception ex)
            {
                _diagnostic = "reflection error: " + ex.Message;
                _reflectionFailed = true;
                Plugin.Log("chart reader disabled: " + _diagnostic);
            }
        }

        // ------------------------------------------------------------ polling

        /// <summary>
        /// Called every frame. Rebuilds the note list when a new chart appears or the
        /// floor count changes (restart, new level, editor edits).
        /// </summary>
        public void Poll(bool log)
        {
            EnsureReflection();
            if (!_reflectionReady)
                return;

            object lm = ReadStatic(_lmInstance);
            if (lm == null)
            {
                // Level not built yet. Drop a stale chart so we never draw the previous song.
                if (_notes.Count > 0)
                {
                    Reset();
                    if (log) Plugin.Log("chart cleared (no level)");
                }
                _diagnostic = "waiting: scrLevelMaker.instance is null";
                return;
            }

            object raw = _lmListFloors.GetValue(lm);
            var floors = raw as IList;
            if (floors == null || floors.Count == 0)
            {
                if (_notes.Count > 0)
                {
                    Reset();
                    if (log) Plugin.Log("chart cleared (empty floor list)");
                }
                _diagnostic = "waiting: listFloors empty or not a list";
                return;
            }

            // Rebuild only when the chart actually changed.
            if (ReferenceEquals(raw, _lastFloorList) && floors.Count == _lastFloorCount)
                return;

            _lastFloorList = raw;
            _lastFloorCount = floors.Count;
            Build(floors, log);
        }

        // ------------------------------------------------------------ run state

        /// <summary>Reads scrController.currentState as a string, or "" when unavailable.</summary>
        private string CurrentState()
        {
            object ctrl = ReadStatic(_ctrlInstance);
            if (ctrl == null || _ctrlCurrentState == null)
                return "";
            try
            {
                object v = _ctrlCurrentState.GetValue(ctrl);
                return v == null ? "" : v.ToString();
            }
            catch
            {
                return "";
            }
        }

        /// <summary>
        /// True while the run is over (death or clear) or the player paused. The overlay must
        /// hold still in these states: the audio clock keeps running behind the death screen,
        /// so without this every note slides off the bottom of the screen while you are dead.
        /// </summary>
        public bool IsFrozen()
        {
            object ctrl = ReadStatic(_ctrlInstance);
            if (ctrl != null && _ctrlPaused != null)
            {
                try
                {
                    object v = _ctrlPaused.GetValue(ctrl, null);
                    if (v is bool && (bool)v)
                        return true;
                }
                catch { /* fall through to the state check */ }
            }

            string state = CurrentState();
            for (int i = 0; i < FrozenStates.Length; i++)
            {
                if (state == FrozenStates[i])
                    return true;
            }
            return false;
        }

        /// <summary>True once the music has actually begun; false through the countdown.</summary>
        public bool SongRunning()
        {
            object cond = ReadStatic(_conductorInstance);
            if (cond == null)
                return false;
            if (_conductorHasSongStarted != null)
            {
                try
                {
                    object v = _conductorHasSongStarted.GetValue(cond);
                    if (v is bool)
                        return (bool)v;
                }
                catch { /* fall back to the state check */ }
            }
            string state = CurrentState();
            for (int i = 0; i < PreSongStates.Length; i++)
            {
                if (state == PreSongStates[i])
                    return false;
            }
            return state.Length > 0;
        }

        /// <summary>
        /// One-line snapshot of the live timeline, for the periodic diagnostic log.
        ///
        /// <paramref name="pxPerSecond"/> is the on-screen fall speed, so the visible count is
        /// computed the same way the renderer culls: a note is on screen while its travel time
        /// from the line is under the time it takes to cross the whole screen.
        ///
        /// <paramref name="frame"/> and <paramref name="notes"/> are the two render gates the
        /// caller used this frame, echoed here so a log always says what the screen showed.
        /// </summary>
        public string FrameDiag(float pxPerSecond, float screenHeight, bool frozen,
            bool frame, bool notes)
        {
            double now = DrawTime();
            double win = pxPerSecond > 1f ? screenHeight / pxPerSecond : 3.0;

            int onScreen = 0;
            int drawable = 0;
            int nextIndex = -1;
            for (int i = 0; i < _notes.Count; i++)
            {
                if (ShouldDraw(i))
                    drawable++;

                double d = _notes[i].HitTime - now;
                if (d >= -0.25 && d <= win) onScreen++;
                if (nextIndex < 0 && d >= 0.0) nextIndex = i;
            }

            string next = nextIndex >= 0
                ? string.Format("#{0} in {1:F3}s", nextIndex, _notes[nextIndex].HitTime - now)
                : "none ahead";

            return string.Format(
                "t={0:F3}s raw={1:F3}s win={2:F3}s onscreen={3} {4} frozen={5} | " +
                "seq={6} drawable={7}/{8} | state={9} song={10} live={11} frame={12} notes={13}",
                now, SongPosition(), win, onScreen, next, frozen,
                CurrentSeqId(), drawable, _notes.Count,
                CurrentState(), SongRunning() ? 1 : 0, TimelineLive() ? 1 : 0,
                frame ? 1 : 0, notes ? 1 : 0);
        }

        /// <summary>
        /// The clock the notes are positioned against: the game's own audio clock, raw.
        ///
        /// An earlier revision anchored and smoothed this (to lift the opening note and to ease
        /// to a stop on death). Both were wrong: the raw clock already matches the chart's entry
        /// times, so any offset shifts the whole timeline off the music. The raw read is the
        /// correct behaviour, and is what this returns again.
        /// </summary>
        public double DrawTime()
        {
            return SongPosition();
        }

        /// <summary>The clock the overlay last rendered against.</summary>
        public double RenderTime()
        {
            return _displayTimeValid ? _displayTime : SongPosition();
        }

        /// <summary>
        /// How long a note keeps existing after it reaches the line, so the strike pop can play.
        /// The caller draws that window differently - pinned to the line, not falling.
        /// </summary>
        public const double StrikePopSeconds = 0.30;

        /// <summary>
        /// Whether the overlay should draw this note at all.
        ///
        /// Time driven throughout: the game's judgement is never consulted, so a note behaves
        /// identically whether it was struck or missed.
        /// </summary>
        public bool ShouldDraw(int index)
        {
            if (index < 0 || index >= _notes.Count)
                return false;
            if (_cleared.Contains(index))
                return false;

            // The very first note is never drawn. One missing note at the start of a chart costs
            // nothing, and it avoids a boundary case that kept showing it a frame early.
            if (index == 0)
                return false;

            // Where this run began, inclusive. The starting tile is skipped as well: it is under
            // the planet the moment play begins, so drawing it only ever puts a note on the line
            // with no time to read it.
            if (_runStartSeqId > 0 && _notes[index].SeqId <= _runStartSeqId)
                return false;

            // Past the line the note survives only long enough to play its strike pop - and, if it is
            // a hold, for the whole hold as well. A hold's bar is still travelling towards the
            // judgement line (and being eaten by it) until the next tile arrives, and the head's
            // strike feedback deliberately stays up for as long as the key is held, so dropping the
            // note at the ordinary pop window would cut both of them off in the middle.
            double survive = StrikePopSeconds + Math.Max(0.0, _notes[index].HoldSeconds);
            if (_notes[index].HitTime <= RenderTime() - survive)
                return false;

            return true;
        }

        /// <summary>
        /// seqID this run started from, or -1 before the run has begun. Latched, never advanced:
        /// using the game's moving position here made the starting tile appear for one frame and
        /// then vanish, which read as "one extra note at the beginning".
        /// </summary>
        private int _runStartSeqId = -1;

        /// <summary>
        /// Called once per frame by the overlay. Latches the run's starting tile when play
        /// begins, and releases it whenever the overlay is not drawing.
        /// </summary>
        public void NotePlayState(bool playing)
        {
            if (!playing)
            {
                _runStartSeqId = -1;
                return;
            }

            if (_runStartSeqId >= 0)
                return;

            int current = CurrentSeqId();
            if (current >= 0)
                _runStartSeqId = current;
        }


        /// <summary>
        /// True whenever a level is being played - the countdown included, the editor's built-in
        /// play mode, and a pause.
        ///
        /// The three cases that must NOT show the overlay, and what excludes them:
        ///
        ///   * "press any key to start" - the state is already Countdown there, but the song has
        ///     not begun, so <see cref="SongRunning"/> is what excludes it;
        ///   * death and the results screen - Fail, Fail2 and Won;
        ///   * menus and the editor's EDIT mode - their states are not in the list below.
        ///
        /// A pause is deliberately NOT one of them. It used to be, which meant the overlay vanished
        /// the moment the player paused to study the chart - the opposite of what pausing is for.
        /// The clock is still held (see <see cref="IsFrozen"/> and AdvanceRenderClock), so the notes
        /// simply stop where they are.
        /// </summary>
        public bool Playing()
        {
            // The editor runs its own play mode, which does not always drive the same state
            // machine as a normal level.
            if (EditorPlaying())
                return true;

            string state = CurrentState();
            if (state == "Fail" || state == "Fail2" || state == "Won")
                return false;

            if (state == "PlayerControl" || state == "Checkpoint")
                return true;

            // Countdown covers both "press any key to start" and the 3-2-1; only the latter has
            // the song clock running.
            return state == "Countdown" && SongRunning();
        }

        /// <summary>
        /// True while a level is loaded and its static frame belongs on screen: the lanes, the
        /// judgement line and the boxes. This is the overlay's outer gate, and it deliberately
        /// includes the "press any key to start" prompt, where the chart is already built but
        /// the run has not begun.
        ///
        /// False in the menus, in the editor outside play mode, and while paused, dead or
        /// finished - the states in which the overlay must disappear entirely.
        /// </summary>
        public bool LevelStanding()
        {
            // Only the end of a run takes the overlay away. Pausing deliberately does not: the
            // player paused to look at the chart, so the frame and the notes stay where they are.
            if (RunEnded())
                return false;
            if (EditorPlaying())
                return true;

            string state = CurrentState();
            for (int i = 0; i < StandingStates.Length; i++)
            {
                if (state == StandingStates[i])
                    return true;
            }
            return false;
        }

        /// <summary>
        /// True once this attempt's note timeline is live: the game's clock has moved on from
        /// where it stood when the chart was set up, and has reached the chart's own zero.
        ///
        /// This is what keeps the notes off the "press any key to start" prompt. The prompt
        /// leaves <c>scrConductor.songposition_minusi</c> holding the previous attempt's
        /// position - or pre-rolling towards zero - while scrController already reports a state
        /// the notes are allowed in, so the clock is the dependable signal here. The conductor's
        /// hasSongStarted flag does not separate the two: the notes were still being drawn at
        /// the prompt, which means that flag already reads true by then.
        /// </summary>
        public bool TimelineLive()
        {
            return _clockMoved && SongPosition() >= 0.0;
        }

        /// <summary>
        /// True while the level editor is in its built-in play mode. scnEditor is a singleton,
        /// and playMode is false whenever the editor is being used to build rather than test.
        /// </summary>
        private bool EditorPlaying()
        {
            object editor = ReadStatic(_editorInstance);
            if (editor == null)
                return false;

            try
            {
                if (_editorPausedInPlay != null)
                {
                    object p = _editorPausedInPlay.GetValue(editor, null);
                    if (p is bool && (bool)p)
                        return false;
                }
                if (_editorPlayMode != null)
                {
                    object m = _editorPlayMode.GetValue(editor, null);
                    return m is bool && (bool)m;
                }
            }
            catch { /* fall through to "not playing" */ }
            return false;
        }

        /// <summary>
        /// True once the game has entered a death or clear state. Pausing is deliberately not
        /// included: pausing should hold the picture, not wipe it.
        /// </summary>
        public bool RunEnded()
        {
            string state = CurrentState();
            return state == "Fail" || state == "Fail2" || state == "Won";
        }

        /// <summary>Raw state name, for diagnostics.</summary>
        public string StateName { get { return CurrentState(); } }

        /// <summary>
        /// True on the frame the run left a death/clear state, i.e. the player revived, retried
        /// or restarted.
        ///
        /// This is the only dependable "new attempt" signal. Watching for a chart rebuild does
        /// not work: a checkpoint revive reuses the same floor objects, so the note list is never
        /// rebuilt and everything the death cleared stays cleared.
        /// </summary>
        public bool RunRestarted()
        {
            string state = CurrentState();
            if (state.Length == 0)
                return false;

            bool ended = state == "Fail" || state == "Fail2" || state == "Won";
            bool restarted = _sawRunEnd && !ended;
            _sawRunEnd = ended;
            return restarted;
        }

        private bool _sawRunEnd;

        /// <summary>Increments whenever the note list is rebuilt (a restart or a new level).</summary>
        public int Generation { get { return _generation; } }

        /// <summary>
        /// Drops every note from the display. Used when the run ends: the chart objects all still
        /// exist, so the only reliable way to stop showing notes on the death screen is to stop
        /// drawing them.
        ///
        /// The cleared set is reset when the chart is rebuilt, which is what a retry does.
        /// </summary>
        public void ClearNotes()
        {
            if (_cleared.Count == _notes.Count)
                return;
            _cleared.Clear();
            for (int i = 0; i < _notes.Count; i++)
                _cleared.Add(i);
        }

        /// <summary>
        /// Brings the notes back after a death. Deliberately does not touch the generation: the
        /// chart itself has not changed, so the finger plan is still valid.
        /// </summary>
        public void RestoreNotes()
        {
            _cleared.Clear();
        }

        /// <summary>True while the overlay clock is held still.</summary>
        public bool ClockFrozen { get { return !double.IsNaN(_frozenAt); } }

        /// <summary>
        /// Resets the render clock. Called when the chart itself changes.
        /// </summary>
        public void ResetRenderClock()
        {
            _cleared.Clear();
            _frozenAt = double.NaN;
            _previousRaw = double.NaN;
            _clockMoved = false;
            _runStartSeqId = -1;
        }

        /// <summary>
        /// The clock the overlay renders against.
        ///
        /// While the run is over or paused the clock stops, so the notes hold position instead
        /// of sliding off the bottom.
        /// </summary>
        public double AdvanceRenderClock(bool frozen)
        {
            double raw = SongPosition();

            // Resuming: restart the clock from wherever the game is now, so a long pause or a
            // death does not cause a jump when play continues.
            if (!frozen && !double.IsNaN(_frozenAt))
            {
                _frozenAt = double.NaN;
                _displayTimeValid = false;
            }

            if (frozen && double.IsNaN(_frozenAt))
                _frozenAt = raw;

            if (double.IsNaN(_frozenAt))
            {
                _displayTime = raw;
                _displayTimeValid = true;
            }

            // Liveness, for TimelineLive: a clock that has not moved at all - the "press any key to
            // start" prompt, still holding the previous attempt's position - is not a timeline, and
            // neither is one pre-rolling towards the chart's zero. A pause is different: the notes
            // are meant to hold where they are, so the latch stays set and they keep being drawn.
            if (raw < 0.0)
                _clockMoved = false;
            else if (!double.IsNaN(_previousRaw) && Math.Abs(raw - _previousRaw) > 1e-6)
                _clockMoved = true;

            _previousRaw = raw;
            return _displayTimeValid ? _displayTime : raw;
        }

        private void Build(IList floors, bool log)
        {
            _notes.Clear();
            _firstHitTime = 0.0;
            _lastHitTime = 0.0;
            _cleared.Clear();
            _generation++;

            // The render clock is indexed by note position, so it is meaningless for a chart
            // that has just been replaced. Leaving it stale made every note in a shorter chart
            // fall behind the cursor and vanish.
            ResetRenderClock();

            int n = floors.Count;
            var entryTimes = new double[n];
            var seqIds = new int[n];

            for (int i = 0; i < n; i++)
            {
                object f = floors[i];
                if (f == null) continue;
                entryTimes[i] = _floorEntryTime != null ? ToDouble(_floorEntryTime.GetValue(f)) : 0.0;
                seqIds[i] = _floorSeqId != null ? (int)ToDouble(_floorSeqId.GetValue(f)) : i;
            }

            for (int i = 0; i < n; i++)
            {
                object f = floors[i];
                if (f == null) continue;

                // Multipress tiles ("多次按键方块"): a tile can need more than one press. The
                // game keeps that count in scrFloor.tapsNeeded (1 on ordinary tiles), so one
                // note is emitted per press - otherwise the whole tile collapses into a single
                // note and the overlay cannot show it as a multipress.
                int taps = 1;
                if (_floorTapsNeeded != null)
                {
                    int needed = (int)ToDouble(_floorTapsNeeded.GetValue(f));
                    if (needed > 1 && needed <= 64)
                        taps = needed;
                }

                // Holds. scrFloor.holdLength is the game's own hold flag: -1 means "not a hold",
                // 0 or more is a hold of that many extra beats (a Hold event with duration n puts n
                // on this field, and n * 2 beats on the tile). The seconds come later, once the gap
                // to the next tile is known - see the pass after ComputeBeats. Ordinary tiles keep
                // the -1 the struct starts with.
                double holdBeats = -1.0;
                if (_floorHoldLength != null)
                {
                    double holdLength = ToDouble(_floorHoldLength.GetValue(f));
                    if (holdLength >= 0.0)
                    {
                        holdBeats = holdLength * 2.0;
                        // A hold's extra input comes from the hold itself, not from extra taps.
                        taps = 1;
                    }
                }

                // Direction from the entry angle: 0 = clockwise quarter turn in ADOFAI's
                // encoding, which is what scrLevelMaker stores. Every tap of a tile shares it.
                double entryAngle = _floorEntryAngle != null ? ToDouble(_floorEntryAngle.GetValue(f)) : 0.0;

                for (int t = 0; t < taps; t++)
                {
                    var note = new Note();
                    note.HitTime = entryTimes[i];
                    note.SeqId = seqIds[i];
                    note.Floor = f;
                    note.MidSpin = IsMidSpin(f);
                    note.GridAngle = double.NaN;
                    note.Direction = entryAngle == 0.0 ? 1 : 0;
                    note.HoldBeats = holdBeats;
                    _notes.Add(note);
                }
            }

            // ---- midspins: converted into normal tiles ---------------------------
            //
            // scrFloor.midSpin is set by the game on the floor BEFORE a zero-length tile: an
            // angle-data 999 (or '!' in a path) makes the game build one extra floor that moves no
            // angle at all, and the game marks the floor in front of it. The planet does not stop on
            // that pair, so it is ONE press - but it is two floors, and the timeline has a spare entry
            // for it.
            //
            // The pair is converted into one normal tile instead of being deleted:
            //
            //   * the two floors are one press, so only one of them survives: the EARLIER floor, which
            //     is the one the game flagged. That is the floor the planet is standing on when the
            //     press lands (currFloor.midSpin is what the game gives the infinite margin to), so it
            //     is the tile being hit, and a hold that rode on it stays with it;
            //   * the angle moves over, though. The game gives the flagged floor a length of zero
            //     (CalculateSingleFloorAngleLength zeroes a midSpin floor's angle) and spends the
            //     pair's rotation on the midspin tile behind it, whose entry angle is half a turn
            //     further round. So the survivor keeps that tile's angle for the beat grid
            //     (Note.GridAngle), which is what makes the pair measure its true 360 degrees instead
            //     of 180 - one beat short, which is what "turn 176.9 deg" in the old log line was: a
            //     180-degree pair with the game's radians fed into a formula that wants degrees;
            //   * it is no longer flagged midspin. From here on it is an ordinary tile, so the
            //     hand-split planner sees exactly one ordinary press and its windows - count, pattern,
            //     hand rule - are computed from the same timeline as any other chart, with nothing
            //     special-casing a midspin anywhere;
            //   * nothing is lost with the removed floor. Its time is the survivor's time (the pair
            //     shares one entry time), and its angle now rides on the survivor, so the survivor's
            //     turn to the next remaining tile is what the game's own timeline gives it.
            //
            // A pair counts as one press when the two floors are within a millisecond of each other -
            // real tiles are tens of milliseconds apart even at the extreme BPM this planner rolls at.
            // Two floors further apart are two real presses and are left completely alone (and logged,
            // because that is the one case where the planner sees a press the game does not ask for).
            // Every fold is logged with both seqIds, the time between them and the turn the survivor
            // measures now, so a chart where a midspin really does carry time is obvious in the log.
            int flagged = 0;
            for (int i = 0; i < _notes.Count; i++)
            {
                if (_notes[i].MidSpin)
                    flagged++;
            }

            int folded = 0;
            int kept = 0;
            for (int i = _notes.Count - 1; i > 0; i--)
            {
                Note later = _notes[i];
                Note earlier = _notes[i - 1];
                if (!later.MidSpin && !earlier.MidSpin)
                    continue;

                double gap = later.HitTime - earlier.HitTime;
                if (Math.Abs(gap) >= 0.001)
                {
                    // A pair this far apart is not one press: it carries time of its own and both
                    // floors are real. Left alone, and said out loud - a chart where this happens is
                    // the one case where the mod's timeline has a press the game does not ask for.
                    kept++;
                    if (kept <= 8)
                        Plugin.Log(string.Format(
                            "chart midspin: seqID {0}+{1} NOT folded, {2:F6}s apart - kept as two presses",
                            earlier.SeqId, later.SeqId, gap));
                    continue;
                }

                // The flagged floor stays: it is the floor the planet is on when the press lands
                // (scrPlayer's currFloor.midSpin is what gets the infinite margin), so it is the tile
                // the player hits, and any hold that rode on it stays with it. What moves over is the
                // ANGLE the beat grid reads - the rotation belongs to the midspin tile behind it.
                earlier.MidSpin = false;
                later.MidSpin = false;
                earlier.GridAngle = AngleOf(later.Floor);
                _notes[i - 1] = earlier;
                _notes.RemoveAt(i);
                folded++;

                if (folded <= 8)
                {
                    // The real number to watch: the surviving note's turn to the next press, measured
                    // from the midspin tile's angle. A midspin is a full 360, and this says whether the
                    // pair still measures it - the log used to print 176.9/183.1 here because it fed
                    // the game's radians into a formula that wants degrees.
                    double turn = i < _notes.Count
                        ? TurnDegrees(AngleDegrees(_notes[i - 1].GridAngle),
                                      AngleDegrees(AngleOf(_notes[i].Floor)))
                        : 0.0;
                    Plugin.Log(string.Format(
                        "chart midspin: seqID {0}+{1} folded into one normal tile " +
                        "(dt {2:F6}s, turn to next {3:F1} deg)",
                        earlier.SeqId, later.SeqId, gap, turn));
                }
            }
            if (flagged > 0)
                Plugin.Log(string.Format(
                    "chart midspins: {0} flagged floor(s), {1} folded into normal tiles{2}",
                    flagged, folded,
                    kept > 0 ? ", " + kept + " left as two presses (see above)" : ""));

            if (_notes.Count > 0)
            {
                _firstHitTime = _notes[0].HitTime;
                _lastHitTime = _notes[_notes.Count - 1].HitTime;
            }

            _diagnostic = string.Format("chart read: {0} notes, {1:F3}s..{2:F3}s",
                _notes.Count, _firstHitTime, _lastHitTime);

            // Prove what the game actually handed us. A chart whose entry times are all zero
            // (or not increasing) cannot possibly produce a moving timeline, and guessing at
            // that from the outside wasted several rounds.
            int zeroTimes = 0;
            int backwards = 0;
            for (int i = 0; i < _notes.Count; i++)
            {
                if (Math.Abs(_notes[i].HitTime) < 1e-9) zeroTimes++;
                if (i > 0 && _notes[i].HitTime < _notes[i - 1].HitTime) backwards++;
            }

            Plugin.Log(string.Format(
                "chart: {0} notes, first {1:F3}s, last {2:F3}s, zero-time {3}, non-monotonic {4}",
                _notes.Count, _firstHitTime, _lastHitTime, zeroTimes, backwards));
            Plugin.Log("chart seqIds: " + _notes[0].SeqId + ".." +
                       _notes[_notes.Count - 1].SeqId);

            _tileBpm = new double[_notes.Count];
            _tileBpmKnown = 0;
            ComputeBeats(log);

            // Holds, now that the gap to the next tile is known.
            //
            // The hold lasts the WHOLE gap. This is not a guess: scrHoldRenderer.Start/CreateMesh set
            // tailFloor = startFloor.nextfloor and build the hold's mesh from the held tile to that
            // next floor, so in the game the hold's body runs from the head all the way to the next
            // tile - the key is down until that tile arrives. As a bar in the overlay that means its
            // far end lands exactly on the next note at every moment, which is both what the player
            // reads and what the game draws.
            //
            // (The first version instead split the gap into hold + travel using holdLength, which
            // shortened every bar by one tile of travel and left it floating short of the next note.)
            // (The reference macro still cannot help here: it never reads holdLength at all, and
            // builds its timeline from turn angles, so a hold chart would not even play in time in
            // it.)
            int holds = 0;
            for (int i = 0; i + 1 < _notes.Count; i++)
            {
                Note note = _notes[i];
                if (note.HoldBeats < 0.0)
                    continue;

                double gap = _notes[i + 1].HitTime - note.HitTime;
                if (gap <= 0.0)
                    continue;

                note.HoldSeconds = gap;
                _notes[i] = note;
                holds++;
            }
            if (holds > 0)
                Plugin.Log("chart holds: " + holds + " tiles with holdLength >= 0");

            if (log)
                Plugin.Log(_diagnostic);
        }

        /// <summary>
        /// Works out each note's length in beats, and the beat lines that follow from it.
        ///
        /// A tile lasts one beat per 180 degrees the planet turns between it and the next tile, so
        /// the chart's own angles give the beat structure directly - no BPM, and therefore no
        /// dependence on scrLevelMaker.bpm_forUnityUseOnly, which has never been readable here.
        /// A note spanning more than a beat gets a line interpolated inside it, which is what makes
        /// the grid stay on the chart's beats through long tiles as well as short ones.
        /// </summary>
        private void ComputeBeats(bool log)
        {
            _beatLines.Clear();
            int count = _notes.Count;
            if (count == 0)
                return;

            var angles = new double[count];
            int unreadable = 0;
            for (int i = 0; i < count; i++)
            {
                angles[i] = double.IsNaN(_notes[i].GridAngle)
                    ? AngleOf(_notes[i].Floor)
                    : _notes[i].GridAngle;
                if (double.IsNaN(angles[i]))
                    unreadable++;
            }

            // The angles may arrive in radians. They are degrees on this install, but a whole-chart
            // test is cheap and a silent factor of 57 is an ugly thing to debug from a screenshot.
            bool radians = true;
            for (int i = 0; i < count; i++)
            {
                if (double.IsNaN(angles[i]))
                    continue;
                if (Math.Abs(angles[i]) > 6.5)
                {
                    radians = false;
                    break;
                }
            }

            double liveBpm = TrueBpm();
            double lastBpm = 0.0;
            int tbpmTiles = 0;
            int angleTiles = 0;
            double beatAt = 0.0;
            double minBeats = double.MaxValue;
            double maxBeats = 0.0;
            for (int i = 0; i < count; i++)
            {
                double t0 = _notes[i].HitTime;
                double t1 = i + 1 < count ? _notes[i + 1].HitTime : t0;
                double span = t1 - t0;

                // Prefer the true BPM the game reported while this tile was being played: a tile
                // lasts (span * bpm / 60) beats, so the grid follows the chart through its speed
                // changes instead of being pinned to one tempo. Tiles not reached yet borrow the
                // most recent tempo and take their own the moment the planet arrives.
                double bpm = _tileBpm.Length == count ? _tileBpm[i] : 0.0;
                if (bpm > 0.0)
                {
                    lastBpm = bpm;
                    tbpmTiles++;
                }
                else
                {
                    bpm = lastBpm > 0.0 ? lastBpm : liveBpm;
                }

                double beats;
                if (bpm > 0.0 && span > 1e-9)
                {
                    beats = span * bpm / 60.0;
                }
                else
                {
                    // No tempo to be had anywhere: fall back to the chart's own geometry, where a
                    // full 180-degree turn is one beat.
                    double a1 = ToDegrees(angles[i], radians);
                    double a2 = i + 1 < count ? ToDegrees(angles[i + 1], radians) : a1;
                    beats = double.IsNaN(a1) || double.IsNaN(a2)
                        ? 1.0
                        : TurnDegrees(a1, a2) / 180.0;
                    angleTiles++;
                }
                if (beats < 1e-4)
                    beats = 1e-4;

                Note note = _notes[i];
                note.Beats = beats;
                _notes[i] = note;

                if (beats < minBeats) minBeats = beats;
                if (beats > maxBeats) maxBeats = beats;

                if (_beatLines.Count < 20000)
                {
                    int k = (int)Math.Ceiling(beatAt - 1e-9);
                    for (; k < beatAt + beats - 1e-9; k++)
                    {
                        var line = new BeatLine();
                        line.Time = t0 + ((k - beatAt) / beats) * (t1 - t0);
                        line.Beat = k;
                        _beatLines.Add(line);
                    }
                }

                beatAt += beats;
            }

            if (log)
            {
                Plugin.Log(string.Format(
                    "beats: {0} tiles, {1:F1} beats, per tile {2:F3}..{3:F3} (avg {4:F3}), {5} lines | " +
                    "tbpm {6:F1}, laid out from tbpm {7} / angles {8} tiles, sampled {9}/{0}, " +
                    "radians={10}, angles unreadable={11}",
                    count, beatAt, minBeats, maxBeats, beatAt / count, _beatLines.Count,
                    liveBpm, tbpmTiles, angleTiles, _tileBpmKnown, radians, unreadable));
            }
        }

        /// <summary>The tile's own angle (scrFloor.entryangle), or NaN when it cannot be read.</summary>
        private double AngleOf(object floor)
        {
            if (floor == null || _floorEntryAngle == null)
                return double.NaN;
            try
            {
                return ToDouble(_floorEntryAngle.GetValue(floor));
            }
            catch
            {
                return double.NaN;
            }
        }

        private static double ToDegrees(double angle, bool radians)
        {
            if (double.IsNaN(angle))
                return double.NaN;
            return radians ? angle * (180.0 / Math.PI) : angle;
        }

        /// <summary>
        /// The same conversion for a single angle whose unit is not known here: anything at or below
        /// a full turn is radians (the game reports radians on this install - 4.712 is the starting
        /// angle), anything larger is already degrees. Used by the midspin log, which has to print a
        /// turn in degrees without <see cref="ComputeBeats"/>' whole-chart radians test.
        /// </summary>
        private static double AngleDegrees(double angle)
        {
            if (double.IsNaN(angle))
                return double.NaN;
            return Math.Abs(angle) <= 7.0 ? angle * (180.0 / Math.PI) : angle;
        }

        /// <summary>
        /// How far the planet turns between two tiles, in degrees, wrapped into (0, 360]. This is
        /// the reference macro's formula (trackAngleDifference), and it is what turns a pair of tile
        /// angles into a length in beats.
        /// </summary>
        private static double TurnDegrees(double a1, double a2)
        {
            return Standardize(Standardize(a1 - 180.0) - a2);
        }

        /// <summary>Wraps an angle into (0, 360], the range ADOFAI's own angles live in.</summary>
        private static double Standardize(double angle)
        {
            angle = angle % 360.0;
            if (angle <= 0.0)
                angle += 360.0;
            return angle;
        }

        // ------------------------------------------------------------ helpers

        /// <summary>
        /// The tempo the chart is really running at: scrConductor.bpm times the song pitch times
        /// the planet speed. This is what JipperResourcePack displays as TBPM, and it is the value
        /// that changes when a chart speeds up or slows down - which is why the beat lines are laid
        /// out from it rather than from any one BPM.
        /// </summary>
        public double TrueBpm()
        {
            double bpm = ReadConductorBpm();
            if (bpm <= 0.0)
                return 0.0;
            return bpm * SongPitch() * PlanetSpeed();
        }

        /// <summary>
        /// The level's BPM as scrConductor reports it - the same field JipperResourcePack builds
        /// TBPM from (TBPM is this times the song pitch times the planet speed). It is the tempo
        /// the chart's speed events multiply, i.e. what the reference macro reads as settings.bpm,
        /// and unlike scrLevelMaker.bpm_forUnityUseOnly it is known to be readable here.
        /// </summary>
        public double ConductorBpm()
        {
            return ReadConductorBpm();
        }

        private double ReadConductorBpm()
        {
            object cond = ReadStatic(_conductorInstance);
            if (cond == null || _conductorBpm == null)
                return 0.0;
            try
            {
                return ToDouble(_conductorBpm.GetValue(cond));
            }
            catch
            {
                return 0.0;
            }
        }

        /// <summary>The song's playback pitch (1 normally), or 1 when it cannot be read.</summary>
        private double SongPitch()
        {
            object cond = ReadStatic(_conductorInstance);
            if (cond == null || _conductorSong == null)
                return 1.0;
            try
            {
                object song = _conductorSong.GetValue(cond);
                if (song == null)
                    return 1.0;
                if (_songPitch == null)
                    _songPitch = song.GetType().GetProperty("pitch", Any);
                if (_songPitch == null)
                    return 1.0;
                double pitch = ToDouble(_songPitch.GetValue(song, null));
                return pitch > 0.0 ? pitch : 1.0;
            }
            catch
            {
                return 1.0;
            }
        }

        /// <summary>
        /// The planet's speed multiplier - the chart's own speed setting, and what makes TBPM differ
        /// from the level's BPM. Newer builds keep it under playerOne.planetarySystem, older ones
        /// directly on the controller, exactly as JipperResourcePack's version-safe layer does it.
        /// Falls back to 1.
        /// </summary>
        private double PlanetSpeed()
        {
            object ctrl = ReadStatic(_ctrlInstance);
            if (ctrl == null)
                return 1.0;

            if (_ctrlPlayerOne != null && _planetSystem != null)
            {
                try
                {
                    object one = _ctrlPlayerOne.GetValue(ctrl);
                    object system = one == null ? null : _planetSystem.GetValue(one);
                    if (system != null)
                    {
                        object raw = _planetSystemSpeedProp != null
                            ? _planetSystemSpeedProp.GetValue(system, null)
                            : (_planetSystemSpeedField != null
                                ? _planetSystemSpeedField.GetValue(system) : null);
                        double speed = ToDouble(raw);
                        if (speed > 0.0)
                            return speed;
                    }
                }
                catch { /* fall through to the older field */ }
            }

            if (_ctrlSpeed != null)
            {
                try
                {
                    double speed = ToDouble(_ctrlSpeed.GetValue(ctrl));
                    if (speed > 0.0)
                        return speed;
                }
                catch { /* fall through */ }
            }
            return 1.0;
        }

        /// <summary>
        /// True BPM sampled while each tile was being played. The value is only knowable live - the
        /// game updates it as speed events come round - so the chart is learned as it is played: a
        /// tile that has not been reached yet is laid out with the most recent tempo, and takes its
        /// own the moment the planet gets there.
        /// </summary>
        private double[] _tileBpm = new double[0];
        private int _tileBpmKnown;

        /// <summary>Files the current TBPM under the note being played; call once a frame.</summary>
        public void SampleBeatTempo(double now)
        {
            if (_notes.Count == 0 || _tileBpm.Length != _notes.Count)
                return;

            double bpm = TrueBpm();
            if (bpm <= 0.0)
                return;

            int note = NoteAt(now);
            if (note < 0 || _tileBpm[note] == bpm)
                return;

            if (_tileBpm[note] <= 0.0)
                _tileBpmKnown++;
            _tileBpm[note] = bpm;
            ComputeBeats(false);
        }

        /// <summary>Index of the note the given clock time falls in, or -1 before the first note.</summary>
        private int NoteAt(double now)
        {
            int lo = 0;
            int hi = _notes.Count - 1;
            int best = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (_notes[mid].HitTime <= now)
                {
                    best = mid;
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }
            return best;
        }

        /// <summary>
        /// The level's own BPM (scrLevelMaker.bpm_forUnityUseOnly), or 0 when it cannot be read.
        ///
        /// The hand-split planner needs it: its windows are measured in beats of this tempo
        /// rather than in fixed milliseconds, which is what lets one fingering survive a chart
        /// that changes speed. This is the chart's base BPM, not the BPM of the tile the planet
        /// happens to be on.
        /// </summary>
        public double LevelBpm()
        {
            if (_lmBpm == null)
                return 0.0;

            try
            {
                object v = _lmBpm.IsStatic
                    ? _lmBpm.GetValue(null)
                    : _lmBpm.GetValue(ReadStatic(_lmInstance));
                return ToDouble(v);
            }
            catch
            {
                return 0.0;
            }
        }

        /// <summary>
        /// A beat length guessed from the chart's own note spacing, for when the game will not
        /// hand its BPM over.
        ///
        /// ADOFAI places notes on beat subdivisions, so the most common gap is a subdivision of a
        /// beat; that gap is then doubled until it lands where a musical beat lives (250 ms to
        /// 1 s, i.e. 60-240 BPM). It is a guess - the beat-grid log line says when this is what is
        /// being used, and the settings panel takes a manual BPM instead.
        /// </summary>
        public double EstimatedBeatSeconds()
        {
            if (_notes.Count < 8)
                return 0.0;

            var buckets = new Dictionary<long, int>();
            for (int i = 0; i + 1 < _notes.Count; i++)
            {
                double gap = _notes[i + 1].HitTime - _notes[i].HitTime;
                if (gap <= 0.0005)
                    continue;                                  // a chord, not a spacing
                long bucket = (long)Math.Round(gap * 200.0) * 5;   // 5 ms buckets
                int seen;
                buckets.TryGetValue(bucket, out seen);
                buckets[bucket] = seen + 1;
            }
            if (buckets.Count == 0)
                return 0.0;

            long common = 0;
            int best = -1;
            foreach (KeyValuePair<long, int> kv in buckets)
            {
                if (kv.Value > best)
                {
                    best = kv.Value;
                    common = kv.Key;
                }
            }

            double beat = common / 1000.0;
            while (beat > 0.0001 && beat < 0.25)
                beat *= 2.0;
            while (beat > 1.0)
                beat /= 2.0;
            return beat;
        }

        /// <summary>
        /// The game's own position in the chart (scrController.currentSeqID). This is what tells
        /// us which tiles have been played, and it is the only reliable signal: scrFloor.grade
        /// initialises to TooEarly (0), not to a "not judged" sentinel, so a grade-based test
        /// treats every note in the chart as already hit and erases the whole overlay.
        /// </summary>
        public int CurrentSeqId()
        {
            object ctrl = ReadStatic(_ctrlInstance);
            if (ctrl == null || _ctrlCurrentSeqId == null)
                return -1;
            try
            {
                return Convert.ToInt32(_ctrlCurrentSeqId.GetValue(ctrl));
            }
            catch
            {
                return -1;
            }
        }

        /// <summary>
        /// The game's judgement for one note, or <see cref="Grade.None"/> when the tile has not
        /// been judged yet. The game resets a tile's grade on rewind and restart, so this
        /// self-heals without the overlay tracking any state of its own.
        /// </summary>
        public Grade GradeOf(int index)
        {
            if (_floorGrade == null || index < 0 || index >= _notes.Count)
                return Grade.None;

            object floor = _notes[index].Floor;
            if (floor == null)
                return Grade.None;

            try
            {
                return (Grade)Convert.ToInt32(_floorGrade.GetValue(floor));
            }
            catch
            {
                return Grade.None;
            }
        }

        /// <summary>True when the game considered this note struck or missed (i.e. it is done).</summary>
        public static bool IsConsumed(Grade g)
        {
            return g != Grade.None;
        }

        private static object ReadStatic(PropertyInfo p)
        {
            if (p == null) return null;
            try { return p.GetValue(null, null); }
            catch { return null; }
        }

        private static object ReadStatic(FieldInfo f)
        {
            if (f == null) return null;
            try { return f.GetValue(null); }
            catch { return null; }
        }

        private bool IsMidSpin(object floor)
        {
            return ReadFlag(floor, _floorMidSpin);
        }

        private bool IsFake(object floor)
        {
            return ReadFlag(floor, _floorIsFake);
        }

        private static bool ReadFlag(object floor, FieldInfo field)
        {
            if (floor == null || field == null)
                return false;
            try
            {
                object v = field.GetValue(floor);
                return v is bool && (bool)v;
            }
            catch
            {
                return false;
            }
        }

        private static double ToDouble(object v)        {
            if (v == null) return 0.0;
            if (v is double) return (double)v;
            if (v is float) return (float)v;
            if (v is int) return (int)v;
            if (v is long) return (long)v;
            try { return Convert.ToDouble(v); }
            catch { return 0.0; }
        }
    }
}
