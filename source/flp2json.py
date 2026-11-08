"""
flp2json - reads an FL Studio project (.flp) and writes what FLP to Video needs as JSON.

    flp2json.exe <project.flp> [out.json]

Uses PyFLP (https://github.com/demberto/PyFLP) for channels, patterns and automation points.
PyFLP 2.2.1 predates FL Studio 25/26, so the event stream is read here with two fixes
(event 172 shrank to 1 byte; playlist items grew to 88 bytes) and the playlist, tracks and
automation links are decoded directly.

Output (all times in ticks of "ppq" per quarter note, from the start of the arrangement):
  tempo:  [[tick, bpm], ...]                 piecewise constant, first entry at tick 0
  notes:  [start, end, key, velocity, flags] flags: 1 step-sequencer hit, 2 slide, 4 muted
  clips:  [start, end, srcStartMs, srcEndMs, flags]  srcEndMs < 0 = untrimmed; flags: 4 muted

Rebuild the exe (Python 3.10 + `pip install pyflp==2.2.1 pyinstaller`):
  pyinstaller --onefile --clean --name flp2json --distpath .. flp2json.py
"""

import io
import json
import os
import struct
import sys
import wave
import warnings

warnings.filterwarnings("ignore")

import construct as c
from pyflp import Project
from pyflp._events import (DATA, DWORD, NEW_TEXT_IDS, TEXT, WORD, AsciiEvent, EventEnum, EventTree,
                           IndexedEvent, U8Event, U16Event, U32Event, UnicodeEvent, UnknownDataEvent)
from pyflp.channel import Automation, Sampler
from pyflp.plugin import PluginID, get_event_by_internal_name
from pyflp.project import FileFormat

SIZE_OVERRIDES = {172: 1}          # FL 26: id 172 carries 1 byte, not 4
PATTERN_BASE = 20480
TEMPO_TARGET = 0x40000005          # automation link target for the project tempo
TEMPO_MIN, TEMPO_MAX = 60.0, 180.0 # FL's tempo automation range
CLIP_MUTED = 0x2000
NOTE_STEP, NOTE_SLIDE, MUTED = 1, 2, 4

EV_PATTERN_NEW, EV_NOTES = 65, 224
EV_ARR_NEW, EV_ARR_CURRENT, EV_ARR_NAME, EV_PLAYLIST = 99, 100, 241, 233
EV_TRACK_DATA, EV_TRACK_NAME, EV_LINK = 238, 239, 227


class FlpError(Exception):
    pass


# ---------------------------------------------------------------------------------------------
#  Raw event stream
# ---------------------------------------------------------------------------------------------

def read_events(data):
    """Yields (id, value) for every event; value is int for fixed-size events, bytes otherwise."""
    if data[:4] != b"FLhd" or data[14:18] != b"FLdt":
        raise FlpError("This isn't an FL Studio project file.")
    p, end = 22, len(data)
    while p < end:
        eid = data[p]
        p += 1
        if eid in SIZE_OVERRIDES:
            n = SIZE_OVERRIDES[eid]
            v = int.from_bytes(data[p:p + n], "little")
            p += n
        elif eid < 64:
            v = data[p]; p += 1
        elif eid < 128:
            v = struct.unpack_from("<H", data, p)[0]; p += 2
        elif eid < 192:
            v = struct.unpack_from("<I", data, p)[0]; p += 4
        else:
            n = sh = 0
            while True:
                b = data[p]; p += 1
                n |= (b & 0x7F) << sh; sh += 7
                if not b & 0x80:
                    break
            v = data[p:p + n]; p += n
        yield eid, v


def pyflp_project(data):
    """pyflp.parse(), with the FL 25/26 size fixes."""
    stream = io.BytesIO(data)
    fmt = int.from_bytes(data[8:10], "little", signed=True)
    channel_count = int.from_bytes(data[10:12], "little")
    ppq = int.from_bytes(data[12:14], "little")
    events = []
    plug_name = None
    str_type = UnicodeEvent
    stream.seek(22)
    while stream.tell() < len(data):
        raw = stream.read(1)[0]
        if raw in SIZE_OVERRIDES:
            stream.read(SIZE_OVERRIDES[raw])
            continue
        id = EventEnum(raw)
        if id < WORD:
            value = stream.read(1)
        elif id < DWORD:
            value = stream.read(2)
        elif id < TEXT:
            value = stream.read(4)
        else:
            value = stream.read(c.VarInt.parse_stream(stream))
        if raw == 199:
            parts = value.decode("ascii").rstrip("\0").split(".")
            str_type = UnicodeEvent if [int(x) for x in parts][:2] >= [11, 5] else AsciiEvent
        event_type = None
        for enum_ in EventEnum.__subclasses__():
            if id in enum_:
                event_type = getattr(enum_(id), "type")
                break
        if event_type is None:
            if id < WORD:
                event_type = U8Event
            elif id < DWORD:
                event_type = U16Event
            elif id < TEXT:
                event_type = U32Event
            elif id < DATA or id.value in NEW_TEXT_IDS:
                event_type = str_type
                if id == PluginID.InternalName:
                    plug_name = event_type(id, value).value
            elif id == PluginID.Data and plug_name is not None:
                event_type = get_event_by_internal_name(plug_name)
            else:
                event_type = UnknownDataEvent
        try:
            events.append(event_type(id, value))
        except Exception:
            if id >= TEXT:
                events.append(UnknownDataEvent(id, value))
    return Project(EventTree(init=(IndexedEvent(r, e) for r, e in enumerate(events))),
                   channel_count=channel_count, format=FileFormat(fmt), ppq=ppq)


def text(v):
    try:
        return v.decode("utf-16-le").rstrip("\0")
    except Exception:
        return v.decode("latin-1", "replace").rstrip("\0")


# ---------------------------------------------------------------------------------------------
#  Project pieces
# ---------------------------------------------------------------------------------------------

def sample_ms(path):
    if not path:
        return None
    path = path.replace("%FLStudioFactoryData%", os.environ.get("FLStudioFactoryData",
                                                                r"C:\Program Files\Image-Line\FL Studio 2026"))
    path = os.path.expandvars(path)
    try:
        with wave.open(path, "rb") as w:
            return w.getnframes() * 1000.0 / w.getframerate()
    except Exception:
        return None


def read_patterns(events):
    """{pattern iid: [(pos, len, key, vel, flags, channel)]} straight from the note records."""
    patterns, cur = {}, None
    for eid, v in events:
        if eid == EV_PATTERN_NEW:
            cur = v
            patterns.setdefault(cur, [])
        elif eid == EV_NOTES and cur is not None:
            notes = []
            for i in range(0, len(v) - 23, 24):
                pos, flags, chan, length, key = struct.unpack_from("<IHHIH", v, i)
                vel = v[i + 21]
                notes.append((pos, length, key, vel, flags, chan))
            patterns[cur] = notes   # the last notes event of a pattern wins
    return patterns


def read_arrangements(events):
    arrs, cur, current = [], None, None
    for eid, v in events:
        if eid == EV_ARR_CURRENT:
            current = v
        elif eid == EV_ARR_NEW:
            cur = {"iid": v, "name": "Arrangement", "items": [], "tracks": {}}
            arrs.append(cur)
            last_track = None
        elif cur is None:
            continue
        elif eid == EV_ARR_NAME:
            cur["name"] = text(v) or cur["name"]
        elif eid == EV_PLAYLIST:
            size = 88 if len(v) % 88 == 0 else 60 if len(v) % 60 == 0 else 32
            for i in range(0, len(v) - size + 1, size):
                pos, _base, idx, length, rv = struct.unpack_from("<IHHIH", v, i)
                flags = struct.unpack_from("<H", v, i + 18)[0]
                so_i, eo_i = struct.unpack_from("<ii", v, i + 24)
                so_f, eo_f = struct.unpack_from("<ff", v, i + 24)
                cur["items"].append({"pos": pos, "len": length, "idx": idx, "track": 499 - rv,
                                     "muted": bool(flags & CLIP_MUTED),
                                     "off_i": (so_i, eo_i), "off_f": (so_f, eo_f)})
        elif eid == EV_TRACK_DATA and len(v) >= 13:
            iid = struct.unpack_from("<I", v, 0)[0]
            last_track = {"enabled": bool(v[12]), "name": ""}
            cur["tracks"][iid - 1] = last_track
        elif eid == EV_TRACK_NAME and last_track is not None:
            last_track["name"] = text(v)
    if not arrs:
        raise FlpError("The project has no playlist arrangement.")
    for a in arrs:
        a["current"] = a["iid"] == current
    if not any(a["current"] for a in arrs):
        arrs[0]["current"] = True
    return arrs


def tempo_links(events):
    chans = set()
    for eid, v in events:
        if eid == EV_LINK and isinstance(v, bytes) and len(v) >= 12:
            if struct.unpack_from("<I", v, 8)[0] == TEMPO_TARGET:
                chans.add(struct.unpack_from("<H", v, 2)[0])
    return chans


def curve(y0, y1, x, tension):
    """Approximation of FL's 'single curve' automation segment (x in 0..1)."""
    if abs(tension) > 1e-6:
        x = x ** (2.0 ** (-3.0 * tension)) if tension < 0 else 1 - (1 - x) ** (2.0 ** (3.0 * tension))
    return y0 + (y1 - y0) * x


def automation_value(points, beat):
    """points: [(beat, value, tension)] sorted. Holds the ends."""
    if beat <= points[0][0]:
        return points[0][1]
    for i in range(1, len(points)):
        b1, v1, t1 = points[i]
        if beat < b1:
            b0, v0, _ = points[i - 1]
            if b1 - b0 <= 1e-9:
                return v1
            return curve(v0, v1, (beat - b0) / (b1 - b0), t1)
    return points[-1][1]


def tempo_map(arr, base_bpm, tempo_points, ppq, song_end):
    """Piecewise-constant [[tick, bpm]] built from the tempo automation clips of one arrangement."""
    clips = []
    for it in arr["items"]:
        if it["idx"] in tempo_points and not it["muted"] and arr["tracks"].get(it["track"], {}).get("enabled", True):
            pts = tempo_points[it["idx"]]
            so, eo = it["off_f"]
            start_beat = so if so == so and so >= 0 else 0.0
            clips.append((it["pos"], it["pos"] + it["len"], start_beat, pts))
    if not clips:
        return [[0, base_bpm]]
    clips.sort(key=lambda c: c[0])

    def bpm_at(tick):
        active = None
        for c0 in clips:
            if c0[0] <= tick:
                active = c0
            else:
                break
        if active is None:
            first = clips[0]
            v = automation_value(first[3], first[2])
        else:
            pos, end, sb, pts = active
            t = min(tick, end)
            v = automation_value(pts, sb + (t - pos) / ppq)
        return TEMPO_MIN + v * (TEMPO_MAX - TEMPO_MIN)

    step = max(1, ppq // 16)
    out = []
    last = None
    for tick in range(0, max(song_end, clips[-1][1]) + step, step):
        bpm = round(bpm_at(tick), 4)
        if last is None or abs(bpm - last) > 1e-3:
            out.append([tick, bpm])
            last = bpm
    return out


# ---------------------------------------------------------------------------------------------
#  Main
# ---------------------------------------------------------------------------------------------

def convert(path):
    with open(path, "rb") as f:
        data = f.read()
    events = list(read_events(data))
    ppq = int.from_bytes(data[12:14], "little")
    proj = pyflp_project(data)

    channels = {}
    tempo_points = {}
    tempo_chans = tempo_links(events)
    for ch in proj.channels:
        kind = type(ch).__name__
        info = {"name": ch.name or ("Channel %d" % (ch.iid + 1)), "kind": kind, "sample": None}
        if isinstance(ch, Sampler):
            try:
                info["sample"] = str(ch.sample_path) if ch.sample_path else None
            except Exception:
                pass
        if isinstance(ch, Automation) and ch.iid in tempo_chans:
            try:
                pts = sorted(((pt.position or 0.0, pt.value or 0.0, pt.tension or 0.0) for pt in ch),
                             key=lambda x: x[0])
                if pts:
                    tempo_points[ch.iid] = pts
            except Exception:
                pass
        channels[ch.iid] = info

    base_bpm = float(proj.tempo or 120.0)
    patterns = read_patterns(events)
    pattern_names = {}
    for pat in proj.patterns:
        try:
            pattern_names[pat.iid] = pat.name
        except Exception:
            pass

    sample_cache = {}

    def sample_len(iid):
        if iid not in sample_cache:
            sample_cache[iid] = sample_ms(channels.get(iid, {}).get("sample"))
        return sample_cache[iid]

    out_arrs = []
    for arr in read_arrangements(events):
        parts = {}

        def part(iid, kind):
            key = (iid, kind)
            if key not in parts:
                ch = channels.get(iid, {"name": "Channel %d" % (iid + 1), "kind": "?", "sample": None})
                parts[key] = {"id": "%d:%s" % (iid, kind), "channel": iid, "name": ch["name"],
                              "kind": kind, "sample": ch["sample"], "sampleMs": sample_len(iid),
                              "items": []}
            return parts[key]

        song_end = 0
        muted_tracks = set()
        muted_clips = 0
        for it in arr["items"]:
            track_on = arr["tracks"].get(it["track"], {}).get("enabled", True)
            muted = it["muted"] or not track_on
            if not track_on:
                muted_tracks.add(it["track"])
            elif it["muted"]:
                muted_clips += 1
            pos, length = it["pos"], it["len"]
            song_end = max(song_end, pos + length)
            mflag = MUTED if muted else 0

            if it["idx"] > PATTERN_BASE:                 # pattern clip
                notes = patterns.get(it["idx"] - PATTERN_BASE, [])
                so, eo = it["off_i"]
                if so < 0 or eo < 0:
                    so, eo = 0, length
                for npos, nlen, key, vel, nflags, chan in notes:
                    if npos < so or npos >= eo:
                        continue
                    start = pos + npos - so
                    end = min(start + nlen, pos + length)
                    flags = mflag
                    if nlen == 0:
                        flags |= NOTE_STEP
                    if nflags & 0x08:
                        flags |= NOTE_SLIDE
                    part(chan, "notes")["items"].append([start, end, key, vel, flags])
            else:                                        # audio / automation clip
                ch = channels.get(it["idx"])
                if ch is None or ch["kind"] == "Automation":
                    continue
                so, eo = it["off_f"]
                if not (so == so) or so < 0:             # NaN or -1 = untrimmed
                    so, eo = 0.0, -1.0
                if not (eo == eo) or eo <= so:
                    eo = -1.0
                part(it["idx"], "clips")["items"].append([pos, pos + length, round(so, 4), round(eo, 4), mflag])

        out_parts = []
        for p in parts.values():
            p["items"].sort(key=lambda n: (n[0], n[1]))
            if p["items"]:
                out_parts.append(p)
        out_parts.sort(key=lambda p: (p["channel"], p["kind"]))

        names = {i: t["name"] for i, t in arr["tracks"].items() if t["name"]}
        out_arrs.append({
            "name": arr["name"], "current": arr["current"],
            "tempo": tempo_map(arr, base_bpm, tempo_points, ppq, song_end),
            "mutedTracks": [names.get(t, "Track %d" % (t + 1)) for t in sorted(muted_tracks)],
            "mutedClips": muted_clips,
            "parts": out_parts,
        })

    return {"file": os.path.abspath(path), "version": proj.version and str(proj.version) or "",
            "ppq": ppq, "bpm": base_bpm, "arrangements": out_arrs}


def main(argv):
    if len(argv) < 2:
        sys.stderr.write("usage: flp2json <project.flp> [out.json]\n")
        return 2
    try:
        result = convert(argv[1])
    except FlpError as e:
        sys.stderr.write(str(e) + "\n")
        return 1
    except Exception as e:
        sys.stderr.write("Couldn't read the project: %s: %s\n" % (type(e).__name__, e))
        return 1
    text_out = json.dumps(result, ensure_ascii=False, separators=(",", ":"))
    if len(argv) > 2:
        with open(argv[2], "w", encoding="utf-8") as f:
            f.write(text_out)
    else:
        sys.stdout.reconfigure(encoding="utf-8")
        sys.stdout.write(text_out)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
