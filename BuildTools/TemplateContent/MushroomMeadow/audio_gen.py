"""Mushroom Meadow template audio generator: phase-1 sound effects, Meadow/Title themes, Meadow Ambience.

The two themes are SNES-style. The sound effects have the game's own soft, woodland palette (kalimba,
marimba, glass, wood blocks, singing bowls, plucked strings, breathy flute, filtered-noise foley; see
"Organic instruments") and deliberately no pulse-wave console sounds or classic-platformer jingles.

Deterministic: re-running produces byte-identical WAVs. Writes every sound into
Source/Genesis.Application.Core/Projects/Templates/Assets/TwoDShowcase/Assets/Audio
together with its .audio.json resource document and .audio.json.meta (existing guids kept).

Usage:  python BuildTools/TemplateContent/MushroomMeadow/audio_gen.py
Requires numpy + scipy.
"""

import hashlib
import json
import os
import wave

import numpy as np
from scipy.signal import butter, lfilter

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "..", ".."))
OUT = os.path.join(REPO, "Source", "Genesis.Application.Core", "Projects", "Templates", "Assets",
                   "TwoDShowcase", "Assets", "Audio")

SR_SFX = 44100
SR_MUSIC = 32000   # the SNES DSP's own rate; keeps the template small with no audible loss
SR_AMB = 44100

# --------------------------------------------------------------------------------------------
# Basic building blocks
# --------------------------------------------------------------------------------------------

NOTE_INDEX = {"C": 0, "C#": 1, "Db": 1, "D": 2, "D#": 3, "Eb": 3, "E": 4, "F": 5, "F#": 6, "Gb": 6,
              "G": 7, "G#": 8, "Ab": 8, "A": 9, "A#": 10, "Bb": 10, "B": 11}


def midi(name):
    """'C5' -> 72, 'F#4' -> 66."""
    pitch, octave = name[:-1], int(name[-1])
    return 12 * (octave + 1) + NOTE_INDEX[pitch]


def hz(m):
    return 440.0 * 2.0 ** ((m - 69) / 12.0)


def nhz(name):
    return hz(midi(name))


def as_array(value, n):
    if np.isscalar(value):
        return np.full(n, float(value))
    return np.asarray(value, dtype=float)


def phase_of(freq, n, sr, start=0.0):
    f = as_array(freq, n)
    return start + np.concatenate(([0.0], np.cumsum(f[:-1]))) / sr, f / sr


def _blep(t, dt):
    y = np.zeros_like(t)
    m = t < dt
    x = t[m] / dt[m]
    y[m] = x + x - x * x - 1.0
    m = t > 1.0 - dt
    x = (t[m] - 1.0) / dt[m]
    y[m] = x * x + x + x + 1.0
    return y


def pulse(freq, n, sr, duty=0.5):
    ph, dt = phase_of(freq, n, sr)
    dt = np.clip(dt, 1e-6, 0.5)
    t = ph % 1.0
    y = np.where(t < duty, 1.0, -1.0)
    y += _blep(t, dt)
    y -= _blep((t - duty) % 1.0, dt)
    return y


def triangle(freq, n, sr, steps=32):
    ph, _ = phase_of(freq, n, sr)
    t = ph % 1.0
    y = 4.0 * np.abs(t - 0.5) - 1.0
    # Gentle 5-bit quantisation for a chip flavour, then smoothed by the mix filters.
    return np.round(y * steps / 2) / (steps / 2)


def sine(freq, n, sr):
    ph, _ = phase_of(freq, n, sr)
    return np.sin(2 * np.pi * ph)


def noise(n, rng, hold=1):
    v = rng.uniform(-1.0, 1.0, (n + hold - 1) // hold)
    return np.repeat(v, hold)[:n]


def env_adsr(n, sr, a=0.004, d=0.08, s=0.6, r=0.04, gate=None):
    """ADSR over n samples; release starts at gate seconds (default: n minus release)."""
    t = np.arange(n) / sr
    gate = (n / sr - r) if gate is None else gate
    e = np.where(t < a, t / max(a, 1e-6), s + (1 - s) * np.exp(-(t - a) / max(d, 1e-6)))
    rel = np.clip(1.0 - (t - gate) / max(r, 1e-6), 0.0, 1.0)
    return e * np.where(t > gate, rel, 1.0)


def env_exp(n, sr, decay, a=0.002):
    t = np.arange(n) / sr
    return np.minimum(1.0, t / a) * np.exp(-t / decay)


def fade_tail(x, sr, seconds=0.01):
    k = min(len(x), int(seconds * sr))
    if k > 0:
        x[-k:] *= np.linspace(1.0, 0.0, k)
    return x


def lowpass(x, sr, fc, order=2, circular=False):
    b, a = butter(order, min(fc / (sr / 2), 0.99), btype="low")
    if circular:
        return lfilter(b, a, np.concatenate((x, x)))[len(x):]
    return lfilter(b, a, x)


def highpass(x, sr, fc, order=2, circular=False):
    b, a = butter(order, fc / (sr / 2), btype="high")
    if circular:
        return lfilter(b, a, np.concatenate((x, x)))[len(x):]
    return lfilter(b, a, x)


def bandpass(x, sr, lo, hi, order=2):
    b, a = butter(order, [lo / (sr / 2), hi / (sr / 2)], btype="band")
    return lfilter(b, a, x)


def echo(x, sr, delay, feedback, taps=5, circular=False, damp=5000.0):
    """SNES-style echo: repeated, darkened copies. Circular wraps for seamless loops."""
    d = int(delay * sr)
    if circular:
        y = x.copy()
        tap = x
        for _ in range(taps):
            tap = lowpass(np.roll(tap, d), sr, damp, order=1, circular=True) * feedback
            y = y + tap
        return y
    y = np.concatenate((x, np.zeros(d * taps)))
    tap = y.copy()
    out = y.copy()
    for _ in range(taps):
        tap = lowpass(np.concatenate((np.zeros(d), tap[:-d])), sr, damp, order=1) * feedback
        out += tap
    return out


def place(buf, x, start, wrap=False):
    start = int(start)
    if wrap:
        n = len(buf)
        idx = (start + np.arange(len(x))) % n
        np.add.at(buf, idx, x)
    else:
        end = min(len(buf), start + len(x))
        if end > start:
            buf[start:end] += x[:end - start]


def normalise(x, peak_db, loop):
    if loop:
        x = x - np.mean(x)      # a constant offset is harmless in a loop; a one-shot would click
    peak = np.max(np.abs(x))
    return x * (10 ** (peak_db / 20.0) / peak) if peak > 0 else x


def write_wav(name, x, sr, peak_db, loop):
    x = normalise(np.asarray(x, dtype=float), peak_db, loop)
    data = np.clip(np.round(x * 32767.0), -32768, 32767).astype("<i2")
    path = os.path.join(OUT, name + ".wav")
    with wave.open(path, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(sr)
        w.writeframes(data.tobytes())
    return path


# --------------------------------------------------------------------------------------------
# Instruments shared by SFX and music
# --------------------------------------------------------------------------------------------

def tone(kind, freq, dur, sr, duty=0.5, env=None, vibrato=0.0, vib_rate=5.5, vib_delay=0.12):
    n = max(1, int(dur * sr))
    f = as_array(freq, n)
    if vibrato > 0:
        t = np.arange(n) / sr
        depth = np.clip((t - vib_delay) / 0.15, 0, 1) * vibrato
        f = f * (2 ** (depth * np.sin(2 * np.pi * vib_rate * t) / 12.0))
    if kind == "pulse":
        y = pulse(f, n, sr, duty)
    elif kind == "tri":
        y = triangle(f, n, sr)
    else:
        y = sine(f, n, sr)
    if env is not None:
        y = y * env
    return y


def sweep(f0, f1, n, curve="exp"):
    if curve == "exp":
        return f0 * (f1 / f0) ** np.linspace(0, 1, n)
    return np.linspace(f0, f1, n)


# --------------------------------------------------------------------------------------------
# Organic instruments for the sound effects
#
# Mushroom Meadow's effects deliberately avoid the pulse-wave console palette: everything below is
# struck wood, glass, kalimba tines, marimba bars and singing bowls (modal synthesis), plucked
# strings (Karplus-Strong), a breathy wood flute and filtered noise for whooshes, puffs and rustles.
# The music in this file never calls these helpers, so it is unaffected by them.
# --------------------------------------------------------------------------------------------

MODES = {
    # (frequency ratio, gain, decay as a fraction of the fundamental's decay)
    "marimba": ((1.0, 1.0, 1.0), (3.93, 0.30, 0.25), (9.85, 0.09, 0.08)),
    "kalimba": ((1.0, 1.0, 1.0), (5.93, 0.24, 0.14), (13.05, 0.07, 0.05)),
    "glass": ((1.0, 1.0, 1.0), (2.32, 0.42, 0.55), (4.25, 0.22, 0.32), (6.63, 0.10, 0.18)),
    "wood": ((1.0, 1.0, 1.0), (2.57, 0.55, 0.6), (4.62, 0.30, 0.4), (6.93, 0.12, 0.3)),
    "bowl": ((1.0, 1.0, 1.0), (2.71, 0.45, 0.6), (5.15, 0.22, 0.4), (8.02, 0.08, 0.25)),
}


def freq_any(note):
    return nhz(note) if isinstance(note, str) else float(note)


def struck(kind, note, dur, sr, decay, rng=None, mallet=0.15, bend=None, detune=0.0):
    """Modal synthesis of a struck bar, tine, glass rod, wood block or bowl (MODES[kind]).

    decay: seconds for the fundamental; mallet: level of a short filtered-noise contact (needs rng);
    bend: optional per-sample frequency multiplier; detune: cents of a beating twin of every mode."""
    f0 = freq_any(note)
    n = max(16, int(dur * sr))
    t = np.arange(n) / sr
    mult = np.ones(n) if bend is None else as_array(bend, n)
    y = np.zeros(n)
    for ratio, g, dk in MODES[kind]:
        if f0 * ratio * np.max(mult) >= 0.45 * sr:
            continue
        ph = np.cumsum(f0 * ratio * mult) / sr
        e = np.exp(-t / (decay * dk))
        y += g * np.sin(2 * np.pi * ph) * e
        if detune:
            y += 0.5 * g * np.sin(2 * np.pi * ph * 2 ** (detune / 1200.0)) * e
    if mallet and rng is not None:
        m = min(n, int(0.01 * sr))
        hit = lowpass(rng.uniform(-1.0, 1.0, m), sr, min(f0 * 5.0, 0.4 * sr))
        y[:m] += mallet * hit * np.exp(-np.arange(m) / (0.0015 * sr))
    y *= np.minimum(1.0, t / 0.0008)
    return fade_tail(y, sr, min(0.01, dur * 0.2))


def ks_pluck(note, dur, sr, rng, t60=1.0, bright=0.5):
    """Karplus-Strong plucked string, resampled so the pitch is exact."""
    f = freq_any(note)
    period = max(2, int(round(sr / f - 0.5)))
    ratio = (sr / (period + 0.5)) / f
    n = int(dur * sr / ratio) + 2
    g = 0.001 ** (1.0 / (t60 * f))
    buf = np.zeros(n + period + 2)
    exc = lfilter([bright], [1.0, bright - 1.0], rng.uniform(-1.0, 1.0, period))
    buf[1:period + 1] = exc - np.mean(exc)
    pos = period + 1
    while pos < len(buf):
        k = min(period, len(buf) - pos)
        buf[pos:pos + k] = g * 0.5 * (buf[pos - period:pos - period + k] + buf[pos - period - 1:pos - period - 1 + k])
        pos += k
    y = buf[1:n + 1]
    m = int(dur * sr)
    y = np.interp(np.arange(m) / ratio, np.arange(len(y)), y)
    y *= np.minimum(1.0, np.arange(m) / (0.0008 * sr))
    return fade_tail(y, sr, min(0.02, dur * 0.2))


def flute(note, dur, sr, rng, a=0.04, r=0.1, s=0.85, vib=0.12, breath=0.12):
    """Breathy wood flute. note: a name, a frequency, or a per-sample frequency array (then its
    length sets the duration) for slurs and sighs."""
    if isinstance(note, str) or np.isscalar(note):
        n = max(16, int(dur * sr))
        f = np.full(n, freq_any(note))
    else:
        f = np.asarray(note, dtype=float)
        n = len(f)
    t = np.arange(n) / sr
    depth = np.clip((t - 0.1) / 0.2, 0.0, 1.0) * vib
    f = f * 2 ** (depth * np.sin(2 * np.pi * 5.2 * t) / 12.0)
    ph = np.cumsum(f) / sr
    body = np.sin(2 * np.pi * ph) + 0.18 * np.sin(4 * np.pi * ph) + 0.05 * np.sin(6 * np.pi * ph)
    air = bandpass(rng.uniform(-1.0, 1.0, n), sr, 900.0, 6000.0)
    chiff = air * np.exp(-t / 0.025)
    y = (body + breath * air * (0.7 + 0.3 * np.sin(2 * np.pi * ph)) + 0.5 * chiff) * env_adsr(n, sr, a, 0.12, s, r)
    return fade_tail(y, sr, min(0.01, n / sr * 0.2))


def swept(x, fc, sr, q=1.0, low=False):
    """Chamberlin state-variable filter with a per-sample cutoff (whooshes, sighs); band-pass or low-pass."""
    fc = as_array(fc, len(x))
    f = 2.0 * np.sin(np.pi * np.clip(fc, 20.0, sr / 6.5) / sr)
    damp = 1.0 / q
    lo = band = 0.0
    out = np.empty(len(x))
    for i in range(len(x)):
        lo += f[i] * band
        high = x[i] - lo - damp * band
        band += f[i] * high
        out[i] = lo if low else band
    return out


def saw_wave(freq, n, sr):
    """Band-limited sawtooth (for growls; never used as a lead voice)."""
    ph, dt = phase_of(freq, n, sr)
    dt = np.clip(dt, 1e-6, 0.5)
    t = ph % 1.0
    return 2.0 * t - 1.0 - _blep(t, dt)


def swell(n, rise=0.3):
    """Raised-cosine rise over `rise` of the length, raised-cosine fall over the rest."""
    t = np.linspace(0.0, 1.0, n)
    up = 0.5 - 0.5 * np.cos(np.pi * np.clip(t / rise, 0, 1))
    down = 0.5 + 0.5 * np.cos(np.pi * np.clip((t - rise) / (1.0 - rise), 0, 1))
    return np.where(t < rise, up, down)


def layer(sr, *parts):
    """parts: (array, start_seconds[, gain]). Returns a buffer long enough for all of them."""
    n = max(int(p[1] * sr) + len(p[0]) for p in parts)
    buf = np.zeros(n)
    for p in parts:
        place(buf, (p[2] if len(p) > 2 else 1.0) * p[0], int(p[1] * sr))
    return buf


def phrase(seq, bpm, start=0.0):
    """[(note or None, beats)] -> [(note, start_seconds, seconds)]."""
    beat = 60.0 / bpm
    out = []
    pos = start
    for nm, b in seq:
        if nm:
            out.append((nm, pos, b * beat))
        pos += b * beat
    return out


def up(note, semis):
    return hz(midi(note) + semis)


def shaker(rng, sr, gain=1.0):
    m = int(0.06 * sr)
    return gain * bandpass(noise(m, rng), sr, 4000, 9500) * env_adsr(m, sr, 0.008, 0.02, 0.3, 0.03)


def chimes(rng, sr, notes, start, count, gap=0.09, gain=0.2, decay=0.3):
    """A loose wind-chime shower: glass rods picked from `notes`, deterministic jitter, fading out."""
    parts = []
    t0 = start
    for k in range(count):
        nm = notes[int(rng.integers(0, len(notes)))]
        parts.append((struck("glass", nm, 0.8, sr, decay, rng, mallet=0.05), t0, gain * (1 - k / (count + 3.0))))
        t0 += gap * (0.7 + 0.6 * rng.uniform())
    return parts


# --------------------------------------------------------------------------------------------
# Sound effects (phase 1)
# --------------------------------------------------------------------------------------------

def sfx_jump(sr, rng):
    """Leaf-light hop: a short breath swish whose band lifts a little, on a soft woody 'tup'."""
    n = int(0.2 * sr)
    swish = swept(noise(n, rng), sweep(600, 1900, n), sr, q=2.0) * env_adsr(n, sr, 0.012, 0.05, 0.2, 0.1)
    tup = struck("wood", 300, 0.1, sr, 0.03, rng, mallet=0.3)
    m = int(0.05 * sr)
    leaf = highpass(noise(m, rng), sr, 3000) * env_exp(m, sr, 0.01, 0.004)
    y = layer(sr, (swish, 0.0, 1.0), (tup, 0.0, 0.6), (leaf, 0.02, 0.1))
    return fade_tail(lowpass(y, sr, 6000), sr)


def sfx_coin(sr, rng):
    """One bright kalimba tine (G6) struck together with a glass rod a fifth above and a pinch of
    sparkle: a single strike, never two notes in a row."""
    tine = struck("kalimba", "G6", 0.5, sr, 0.16, rng, mallet=0.35)
    rod = struck("glass", "D7", 0.5, sr, 0.22, rng, mallet=0.0, detune=7.0)
    m = int(0.06 * sr)
    sparkle = highpass(noise(m, rng), sr, 7000) * env_exp(m, sr, 0.012, 0.001)
    y = layer(sr, (tine, 0, 1.0), (rod, 0.004, 0.35), (sparkle, 0, 0.12))
    return echo(fade_tail(y, sr), sr, 0.07, 0.12, taps=2)


def sfx_big_coin(sr, rng):
    """Wind-chime shower: a kalimba root and a singing-bowl hum, then glass rods brushed in a loose
    C-pentatonic cascade that tumbles down and back up."""
    order = ["E7", "C7", "A6", "D7", "G6", "C7", "E7", "G7"]
    parts = [(struck("kalimba", "C6", 0.9, sr, 0.3, rng, mallet=0.35), 0.0, 0.8),
             (struck("bowl", "C5", 1.3, sr, 0.6, rng, mallet=0.1, detune=4.0), 0.0, 0.35)]
    t0 = 0.05
    for k, nm in enumerate(order):
        parts.append((struck("glass", nm, 0.8, sr, 0.28, rng, mallet=0.08), t0, 0.42 * (1 - k / 12.0)))
        t0 += 0.055 + 0.03 * rng.uniform()
    y = layer(sr, *parts)
    return echo(fade_tail(lowpass(y, sr, 11000), sr), sr, 0.09, 0.2, taps=3)


def sfx_stomp(sr, rng):
    """Mushroom-cap squash: a soft membrane 'pomf', a puff of spore dust and a dull wooden 'tok'."""
    n = int(0.18 * sr)
    pomf = sine(sweep(170, 70, n), n, sr) * env_exp(n, sr, 0.05, 0.002)
    puff = lowpass(noise(n, rng), sr, 1800) * env_exp(n, sr, 0.03, 0.003)
    tok = struck("wood", 520, 0.1, sr, 0.03, rng, mallet=0.3)
    y = layer(sr, (pomf, 0, 1.0), (puff, 0, 0.6), (tok, 0.005, 0.35))
    return fade_tail(lowpass(y, sr, 8000), sr)


def sfx_bounce(sr, rng):
    """Springy mushroom cap: a rubbery membrane 'bwom' wobbling round one pitch and settling (no
    upward sweep), a cushioned thump, and a soft marimba D5 + A5 on the rebound."""
    n = int(0.5 * sr)
    t = np.arange(n) / sr
    f = 150.0 * (1 + 0.3 * np.exp(-t / 0.12) * np.sin(2 * np.pi * 9.0 * t))
    ph = np.cumsum(f) / sr
    membrane = (np.sin(2 * np.pi * ph) + 0.35 * np.sin(2 * np.pi * ph * 2.3) * np.exp(-t / 0.06)) \
        * env_exp(n, sr, 0.16, 0.004)
    m = int(0.06 * sr)
    thump = lowpass(noise(m, rng), sr, 700) * env_exp(m, sr, 0.015, 0.002)
    dyad = struck("marimba", "D5", 0.5, sr, 0.25, rng, mallet=0.2) \
        + 0.7 * struck("marimba", "A5", 0.5, sr, 0.2, rng, mallet=0.2)
    y = layer(sr, (membrane, 0, 1.0), (thump, 0, 0.5), (dyad, 0.05, 0.35))
    return echo(fade_tail(lowpass(y, sr, 8000), sr), sr, 0.07, 0.15, taps=2)


def sfx_block(sr, rng):
    """Stone giving way: a sharp crack, a low earthy thud and gravel grains skittering off
    (filtered-noise grains, no tonal debris)."""
    n = int(0.38 * sr)
    c = int(0.05 * sr)
    crack = highpass(noise(c, rng), sr, 1200) * env_exp(c, sr, 0.01, 0.0005)
    k = int(0.12 * sr)
    thud = sine(sweep(130, 60, k), k, sr) * env_exp(k, sr, 0.04, 0.001)
    body = lowpass(noise(n, rng, 2), sr, 1800) * env_exp(n, sr, 0.06, 0.001)
    parts = [(crack, 0, 0.9), (thud, 0, 0.9), (body, 0, 0.6)]
    for j in range(16):
        m = int(rng.uniform(0.008, 0.025) * sr)
        lo = rng.uniform(900, 2500)
        g = bandpass(noise(m, rng), sr, lo, lo * 2.2) * env_exp(m, sr, m / sr * 0.35, 0.0006)
        parts.append((g, 0.03 + 0.3 * (j / 16.0) ** 1.3 + rng.uniform(0, 0.015), 0.5 * (1 - j / 20.0)))
    return fade_tail(lowpass(layer(sr, *parts), sr, 8000), sr)


def sfx_bump(sr, rng):
    """Head meets hard wood: a low, felt-damped wood knock with a dull body thud."""
    knock = struck("wood", 190, 0.15, sr, 0.045, rng, mallet=0.5)
    n = int(0.12 * sr)
    body = sine(sweep(110, 80, n), n, sr) * env_exp(n, sr, 0.035, 0.002)
    y = layer(sr, (knock, 0, 0.8), (body, 0, 0.6))
    return fade_tail(lowpass(y, sr, 4500), sr)


def sfx_hurt(sr, rng):
    """'Oof': a breathy flute-voice sigh dropping a minor third with a shudder, a dissonant glass rub
    (F6 against F#6) and a soft body thump."""
    n = int(0.42 * sr)
    t = np.arange(n) / sr
    glide = sweep(nhz("A5"), nhz("F#5"), n) * (1 + 0.025 * np.sin(2 * np.pi * 11 * t))
    sigh = flute(glide, 0.42, sr, rng, a=0.01, r=0.18, s=0.6, vib=0.0, breath=0.35)
    rub = struck("glass", "F6", 0.5, sr, 0.15, rng, mallet=0.0) + struck("glass", "F#6", 0.5, sr, 0.15, rng, mallet=0.0)
    k = int(0.12 * sr)
    thump = sine(sweep(120, 60, k), k, sr) * env_exp(k, sr, 0.035, 0.001)
    puff = lowpass(noise(k, rng), sr, 2000) * env_exp(k, sr, 0.02, 0.001)
    y = layer(sr, (thump, 0, 0.8), (puff, 0, 0.4), (rub, 0, 0.25), (sigh, 0.02, 0.6))
    return echo(fade_tail(lowpass(y, sr, 7000), sr), sr, 0.06, 0.15, taps=2)


def sfx_checkpoint(sr, rng):
    """Lantern lit: the Glowspore motif's opening (sol-do-re-mi) plucked on kalimba, the last tine
    held over a singing-bowl ring, a glass overtone and a breath of warm air."""
    parts = []
    for k, nm in enumerate(("G5", "C6", "D6", "E6")):
        last = k == 3
        parts.append((struck("kalimba", nm, 1.2 if last else 0.5, sr, 0.45 if last else 0.18, rng, mallet=0.3),
                      0.1 * k, 0.7 if last else 0.55))
    parts.append((struck("bowl", "C5", 1.2, sr, 0.45, rng, mallet=0.05, detune=5.0), 0.3, 0.4))
    parts.append((struck("glass", "G6", 1.0, sr, 0.4, rng, mallet=0.0), 0.32, 0.18))
    n = int(1.0 * sr)
    air = swept(noise(n, rng), sweep(600, 1800, n), sr, q=0.9) * swell(n, 0.3)
    parts.append((air, 0.25, 0.12))
    y = layer(sr, *parts)
    return echo(fade_tail(lowpass(y, sr, 9000), sr), sr, 0.12, 0.22, taps=3)


def sfx_pause(sr, rng):
    """Time held still: a soft wood 'tock' and a felt-muted marimba dyad (A4 + E5 struck together)."""
    tock = struck("wood", 880, 0.08, sr, 0.02, rng, mallet=0.3)
    dyad = struck("marimba", "A4", 0.45, sr, 0.14, rng, mallet=0.15) \
        + 0.8 * struck("marimba", "E5", 0.45, sr, 0.12, rng, mallet=0.15)
    y = layer(sr, (tock, 0, 0.5), (dyad, 0.03, 0.8))
    return echo(fade_tail(lowpass(y, sr, 6000), sr), sr, 0.08, 0.15, taps=2)


def sfx_land(sr, rng):
    """Soft footfall in grass: a muffled thump and a brush of leaves."""
    n = int(0.1 * sr)
    y = lowpass(noise(n, rng, 2), sr, 1200) * env_exp(n, sr, 0.025)
    y += 0.6 * sine(sweep(120, 60, n), n, sr) * env_exp(n, sr, 0.03)
    m = int(0.05 * sr)
    y[:m] += 0.12 * bandpass(noise(m, rng), sr, 2500, 7000) * env_exp(m, sr, 0.012, 0.003)
    return fade_tail(highpass(y, sr, 40, order=1), sr)


def sfx_bee_buzz(sr, rng):
    """Seamless 0.5 s wingbeat loop: a band-limited harmonic buzz (176 Hz, wobbling at 26 Hz) with a
    wing-flutter tremolo. Every component completes a whole number of cycles in the loop."""
    dur = 0.5
    n = int(dur * sr)
    t = np.arange(n) / sr
    f0, fm, ftrem = 176.0, 26.0, 8.0          # 88, 13 and 4 cycles in 0.5 s
    ph = f0 * t + 0.35 * np.sin(2 * np.pi * fm * t)
    y = np.zeros(n)
    for k in range(1, 16):
        y += np.sin(2 * np.pi * k * ph + 0.7 * k) / k ** 1.1
    y *= 0.75 + 0.25 * np.sin(2 * np.pi * ftrem * t)
    y = highpass(y, sr, 140, circular=True)
    return lowpass(y, sr, 2400, circular=True)


def sfx_finish(sr, rng):
    """Glowspore cadence: the whole Glowspore motif (sol-do-re-mi-- sol | mi-re-do) on a breathy wood
    flute doubled by kalimba, over rolled marimba chords and a soft shaker, closing on a plucked-harp
    strum, a singing bowl and a wind-chime shower."""
    bpm = 112
    beat = 60.0 / bpm
    lead = phrase([("G5", .5), ("C6", .5), ("D6", .5), ("E6", 1.5), ("G6", 1.0),
                   ("E6", .5), ("D6", .5), ("C6", 3.0)], bpm)
    parts = []
    for nm, t0, d in lead:
        parts.append((flute(nm, d * 0.98 + 0.06, sr, rng, a=0.03, r=0.12, vib=0.18, breath=0.1), t0, 0.5))
        parts.append((struck("kalimba", nm, min(1.2, d + 0.4), sr, 0.25, rng, mallet=0.3), t0, 0.3))
    voice = {"C": ("C3", "G3", "E4"), "Am": ("A2", "E3", "C4"), "F": ("F2", "C3", "A3"), "G": ("G2", "D3", "B3")}
    for b, ch in ((0, "C"), (1, "C"), (2, "Am"), (3, "G"), (4, "F"), (4.5, "G"), (5, "C")):
        for k, nm in enumerate(voice[ch]):
            parts.append((struck("marimba", nm, 0.9, sr, 0.35, rng, mallet=0.2), b * beat + 0.018 * k,
                          0.32 if k == 0 else 0.2))
    for e in range(10):
        parts.append((shaker(rng, sr), e * 0.5 * beat, 0.05 if e % 2 else 0.08))
    end = 5 * beat
    for k, nm in enumerate(("C3", "G3", "C4", "E4", "G4", "C5", "E5")):
        parts.append((ks_pluck(nm, 1.6, sr, rng, t60=1.3, bright=0.55), end + 0.025 * k, 0.16))
    parts.append((struck("bowl", "C4", 2.0, sr, 0.8, rng, mallet=0.05, detune=4.0), end, 0.3))
    parts += chimes(rng, sr, ["G6", "A6", "C7", "D7", "E7", "G7"], end + 0.3, 7, gap=0.12, gain=0.16)
    y = layer(sr, *parts)
    return echo(fade_tail(lowpass(y, sr, 10000), sr, 0.05), sr, 0.14, 0.22, taps=3)


def sfx_game_over(sr, rng):
    """Lantern dims: a slow, sleepy A-minor fall (mi-re-do-la, sol-la) on breathy flute with
    music-box kalimba tines an octave up, over low marimba and a fading singing bowl."""
    bpm = 92
    beat = 60.0 / bpm
    lead = phrase([("E5", .75), ("D5", .75), ("C5", .5), ("A4", 1.5), ("G4", .5), ("A4", 2.5)], bpm)
    parts = []
    for nm, t0, d in lead:
        parts.append((flute(nm, d * 0.98 + 0.05, sr, rng, a=0.06, r=0.25, s=0.75, vib=0.15, breath=0.16),
                      t0, 0.55))
        parts.append((struck("kalimba", up(nm, 12), min(1.4, d + 0.5), sr, 0.35, rng, mallet=0.25), t0, 0.22))
    for b, root in ((0, "A2"), (0.75, "G2"), (1.5, "F2"), (3.5, "E2"), (4.0, "A2")):
        parts.append((struck("marimba", root, 1.4, sr, 0.5, rng, mallet=0.15), b * beat, 0.4))
        parts.append((struck("marimba", up(root, 7), 1.2, sr, 0.4, rng, mallet=0.15), b * beat + 0.02, 0.22))
    parts.append((struck("bowl", "A3", 1.8, sr, 0.55, rng, mallet=0.05, detune=3.0), 4.0 * beat, 0.3))
    y = layer(sr, *parts)
    y = lowpass(y, sr, 6500)
    return echo(fade_tail(y, sr, 0.05), sr, 0.18, 0.25, taps=3)


def sfx_extra_life(sr, rng):
    """A new sprout: one slurred breath on the wood flute rising sol-do-mi (G5-C6-E6, legato), doubled
    by kalimba tines, opening into a glass bloom over a soft singing bowl."""
    seg = int(0.11 * sr)
    hold = int(0.55 * sr)
    f = np.concatenate((np.full(seg, nhz("G5")), np.full(seg, nhz("C6")), np.full(hold, nhz("E6"))))
    k = int(0.012 * sr)
    f = np.convolve(np.pad(f, k, mode="edge"), np.ones(2 * k + 1) / (2 * k + 1), mode="valid")   # portamento
    lead = flute(f, 0, sr, rng, a=0.02, r=0.2, s=0.8, vib=0.2, breath=0.12)
    parts = [(lead, 0, 0.55)]
    for j, nm in enumerate(("G6", "C7", "E7")):
        parts.append((struck("kalimba", nm, 0.6, sr, 0.2, rng, mallet=0.3), 0.11 * j, 0.3))
    parts.append((struck("glass", "G6", 0.9, sr, 0.35, rng, mallet=0.0), 0.24, 0.2))
    parts.append((struck("glass", "E7", 0.8, sr, 0.3, rng, mallet=0.0), 0.26, 0.16))
    parts.append((struck("bowl", "C5", 0.9, sr, 0.3, rng, mallet=0.05, detune=4.0), 0.22, 0.25))
    y = layer(sr, *parts)
    return echo(fade_tail(lowpass(y, sr, 10000), sr), sr, 0.09, 0.2, taps=3)


# --------------------------------------------------------------------------------------------
# Music
# --------------------------------------------------------------------------------------------

CHORDS = {  # chord tones as semitone offsets from root, plus root name
    "C": ("C", (0, 4, 7)), "Am": ("A", (0, 3, 7)), "F": ("F", (0, 4, 7)), "G": ("G", (0, 4, 7)),
    "Em": ("E", (0, 3, 7)), "Dm": ("D", (0, 3, 7)), "G7": ("G", (0, 4, 7, 10)),
}


def chord_root(chord, octave):
    return midi(CHORDS[chord][0] + str(octave))


class Song:
    def __init__(self, bpm, beats, sr):
        self.sr = sr
        self.beat = 60.0 / bpm
        self.length = int(round(beats * self.beat * sr))
        self.tracks = {}

    def track(self, name):
        if name not in self.tracks:
            self.tracks[name] = np.zeros(self.length)
        return self.tracks[name]

    def note(self, track, m, start_beat, beats, kind, duty=0.5, gain=1.0, legato=0.9, decay=None,
             vibrato=0.0, sustain=0.65, attack=0.004):
        sr = self.sr
        dur = beats * self.beat * legato
        n = max(8, int(dur * sr))
        if decay:
            env = env_exp(n, sr, decay, attack)
        else:
            env = env_adsr(n, sr, attack, 0.12, sustain, min(0.05, dur * 0.3))
        env = fade_tail(env.copy(), sr, 0.004)
        y = gain * tone(kind, hz(m), dur, sr, duty, env, vibrato=vibrato)
        place(self.track(track), y, start_beat * self.beat * sr, wrap=True)

    def hit(self, track, x, start_beat):
        place(self.track(track), x, start_beat * self.beat * self.sr, wrap=True)

    def melody(self, track, seq, start_beat, **kw):
        pos = start_beat
        for nm, b in seq:
            if nm:
                self.note(track, midi(nm), pos, b, **kw)
            pos += b
        return pos


def drum_kit(sr, rng):
    n = int(0.16 * sr)
    kick = sine(sweep(160, 48, n), n, sr) * env_exp(n, sr, 0.07)
    kick[: int(0.003 * sr)] += 0.4 * rng.uniform(-1, 1, int(0.003 * sr))
    n = int(0.14 * sr)
    snare = highpass(noise(n, rng, 2), sr, 900) * env_exp(n, sr, 0.045) * 0.8
    snare += 0.5 * triangle(sweep(240, 170, n), n, sr) * env_exp(n, sr, 0.03)
    n = int(0.05 * sr)
    hat = highpass(noise(n, rng), sr, 6000) * env_exp(n, sr, 0.012)
    n = int(0.12 * sr)
    shaker = bandpass(noise(n, rng), sr, 3500, 9000) * env_adsr(n, sr, 0.02, 0.04, 0.3, 0.04)
    return {"kick": fade_tail(kick, sr), "snare": fade_tail(snare, sr), "hat": fade_tail(hat, sr),
            "shaker": fade_tail(shaker, sr)}


def mixdown(song, gains, echo_tracks, delay_beats, feedback, lp=9000.0):
    sr = song.sr
    mix = np.zeros(song.length)
    send = np.zeros(song.length)
    for name, y in song.tracks.items():
        g = gains.get(name, 1.0)
        mix += g * y
        if name in echo_tracks:
            send += g * y * echo_tracks[name]
    mix += echo(send, sr, delay_beats * song.beat, feedback, taps=6, circular=True, damp=4200) - send
    mix = lowpass(mix, sr, lp, order=2, circular=True)
    mix = highpass(mix, sr, 30, order=1, circular=True)
    return mix


def meadow_theme(sr, rng):
    """Cheerful 150 BPM loop in C major: A A' B A'' (32 bars, 51.2 s)."""
    bpm = 150
    song = Song(bpm, 32 * 4, sr)

    a_head = [  # bars 1-6 shared by every A
        ("E5", .5), ("G5", .5), ("C6", 1), ("B5", .5), ("G5", .5), ("E5", 1),
        ("A5", 1), ("G5", .5), ("E5", .5), ("C5", 1), ("E5", 1),
        ("F5", .5), ("A5", .5), ("C6", 1), ("A5", .5), ("F5", .5), ("A5", 1),
        ("G5", 1.5), ("F5", .5), ("E5", .5), ("D5", .5), ("B4", 1),
        ("E5", .5), ("G5", .5), ("C6", 1), ("D6", .5), ("C6", .5), ("B5", 1),
        ("A5", .5), ("C6", .5), ("E6", 1), ("D6", .5), ("C6", .5), ("A5", 1),
    ]
    a_end1 = [("F5", .5), ("A5", .5), ("C6", .5), ("A5", .5), ("G5", .5), ("B5", .5), ("D6", 1),
              ("C6", 2), (None, 1), ("G4", .5), ("B4", .5)]
    a_end2 = [("F5", .5), ("A5", .5), ("C6", .5), ("F6", .5), ("E6", .5), ("D6", .5), ("B5", 1),
              ("C6", 3), (None, 1)]
    a_end3 = [("F5", .5), ("A5", .5), ("C6", .5), ("A5", .5), ("G5", .5), ("B5", .5), ("D6", 1),
              ("C6", 1.5), ("G5", .5), ("E5", .5), ("D5", .5), ("C5", .5), ("D5", .5)]
    b_part = [
        ("C6", 1), ("A5", .5), ("C6", .5), ("F6", 1), ("E6", .5), ("D6", .5),
        ("D6", 1.5), ("B5", .5), ("G5", 1), ("B5", 1),
        ("E6", 1), ("D6", .5), ("B5", .5), ("G5", 1), ("E5", 1),
        ("A5", .5), ("B5", .5), ("C6", .5), ("D6", .5), ("E6", 2),
        ("F6", 1), ("E6", .5), ("D6", .5), ("C6", 1), ("A5", 1),
        ("B5", 1), ("C6", .5), ("D6", .5), ("G5", 2),
        ("F5", .5), ("A5", .5), ("D6", 1), ("C6", .5), ("A5", .5), ("F5", 1),
        ("G5", .5), ("A5", .5), ("B5", .5), ("D6", .5), ("F6", 1), ("D6", .5), ("B5", .5),
    ]
    sections = [("A", a_head + a_end1), ("A2", a_head + a_end2), ("B", b_part), ("A3", a_head + a_end3)]
    a_chords = ["C", "C", "Am", "Am", "F", "F", "G", "G", "C", "C", "Am", "Am", "F", "G", "C", "C"]
    b_chords = ["F", "F", "G", "G", "Em", "Em", "Am", "Am", "F", "F", "G", "G", "Dm", "Dm", "G7", "G7"]

    kit = drum_kit(sr, rng)
    beat0 = 0
    for idx, (name, mel) in enumerate(sections):
        chords = b_chords if name == "B" else a_chords      # one chord per half bar
        # Lead: bright pulse; in the second A the lead doubles an octave down on a thinner duty.
        song.melody("lead", mel, beat0, kind="pulse", duty=0.5 if name != "B" else 0.25, gain=0.55,
                    legato=0.88, vibrato=0.18, sustain=0.55)
        if name in ("A2", "A3"):
            song.melody("lead2", [(n and _shift(n, -12), b) for n, b in mel], beat0, kind="pulse",
                        duty=0.125, gain=0.25, legato=0.85, sustain=0.5)
        for h, chord in enumerate(chords):
            hb = beat0 + h * 2
            root = chord_root(chord, 2)
            fifth = root + 7
            # Bouncy bass: root, octave, fifth, octave per half bar (eighths), triangle channel.
            for k, m in enumerate((root, root + 12, fifth, root + 12)):
                song.note("bass", m, hb + k * 0.5, 0.5, "tri", gain=0.85, legato=0.7, sustain=0.8)
            tones = [chord_root(chord, 4) + o for o in CHORDS[chord][1][:3]]
            if name == "A":
                # Off-beat chord stabs.
                for off in (0.5, 1.5):
                    for m in tones:
                        song.note("chords", m, hb + off, 0.5, "pulse", duty=0.25, gain=0.16, legato=0.5,
                                  decay=0.08)
            else:
                # Sixteenth-note arpeggio, rising then falling.
                pattern = tones + [tones[0] + 12] + [tones[0] + 12, tones[2], tones[1], tones[0]]
                for k, m in enumerate(pattern):
                    song.note("arp", m + 12, hb + k * 0.25, 0.25, "pulse", duty=0.125, gain=0.15,
                              legato=0.8, decay=0.07)
        # Drums: light kick/snare, hats on eighths; first four bars of the loop keep it lighter.
        for bar in range(8):
            bb = beat0 + bar * 4
            song.hit("drums", kit["kick"], bb)
            song.hit("drums", kit["kick"], bb + 2)
            if name == "B" and bar % 2 == 1:
                song.hit("drums", kit["kick"], bb + 2.5)
            song.hit("drums", kit["snare"] * (0.6 if idx == 0 and bar < 4 else 1.0), bb + 1)
            song.hit("drums", kit["snare"], bb + 3)
            for e in range(8):
                song.hit("drums", kit["hat"] * (0.9 if e % 2 else 0.5), bb + e * 0.5)
        # Fill into the next section.
        fill_beat = beat0 + 31
        for k in range(4):
            song.hit("drums", kit["snare"] * (0.5 + 0.12 * k), fill_beat + k * 0.25)
        beat0 += 32

    mix = mixdown(song, {"lead": 1.0, "lead2": 1.0, "bass": 0.9, "chords": 1.0, "arp": 1.0, "drums": 0.55},
                  {"lead": 0.35, "arp": 0.4, "chords": 0.3}, delay_beats=0.75, feedback=0.3)
    return mix


def _shift(name, semis):
    m = midi(name) + semis
    names = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"]
    return names[m % 12] + str(m // 12 - 1)


def title_theme(sr, rng):
    """Calm 80 BPM loop: 8 bars (24 s), lyrical lead over harp arpeggios and a soft pad."""
    bpm = 80
    song = Song(bpm, 8 * 4, sr)
    mel = [
        ("G5", 1.5), ("E5", .5), ("C5", 1), ("E5", 1),
        ("G5", 1.5), ("B5", .5), ("G5", 2),
        ("A5", 1.5), ("C6", .5), ("A5", 1), ("F5", 1),
        ("G5", 3), ("E5", 1),
        ("E5", 1.5), ("A5", .5), ("C6", 1), ("B5", 1),
        ("A5", 1.5), ("G5", .5), ("F5", 1), ("A5", 1),
        ("D6", 1.5), ("C6", .5), ("A5", 1), ("F5", 1),
        ("G5", 2), ("F5", 1), ("D5", 1),
    ]
    chords = ["C", "Em", "F", "C", "Am", "F", "Dm", "G"]
    song.melody("lead", mel, 0, kind="tri", gain=0.7, legato=0.95, vibrato=0.25, sustain=0.75, attack=0.02)
    song.melody("lead2", mel, 0, kind="pulse", duty=0.125, gain=0.12, legato=0.9, vibrato=0.25,
                sustain=0.6, attack=0.03)
    kit = drum_kit(sr, rng)
    for bar, chord in enumerate(chords):
        bb = bar * 4
        root = chord_root(chord, 2)
        song.note("bass", root, bb, 2, "tri", gain=0.8, legato=0.95, sustain=0.7)
        song.note("bass", root + 7, bb + 2, 2, "tri", gain=0.7, legato=0.95, sustain=0.7)
        tones = [chord_root(chord, 4) + o for o in CHORDS[chord][1][:3]]
        for m in tones:
            song.note("pad", m - 12, bb, 4, "pulse", duty=0.5, gain=0.07, legato=0.98, sustain=0.85,
                      attack=0.25)
        harp = [tones[0], tones[1], tones[2], tones[0] + 12, tones[1] + 12, tones[0] + 12, tones[2], tones[1]]
        for k, m in enumerate(harp):
            song.note("harp", m, bb + k * 0.5, 0.5, "pulse", duty=0.25, gain=0.17, legato=1.0, decay=0.22)
        for e in (1, 3):
            song.hit("perc", kit["shaker"] * 0.35, bb + e)
    song.tracks["pad"] = lowpass(song.tracks["pad"], sr, 1800, circular=True)
    return mixdown(song, {"lead": 1, "lead2": 1, "bass": 0.8, "pad": 1, "harp": 1, "perc": 1},
                   {"lead": 0.5, "lead2": 0.5, "harp": 0.5}, delay_beats=0.75, feedback=0.4, lp=8000)


# --------------------------------------------------------------------------------------------
# Ambience
# --------------------------------------------------------------------------------------------

def meadow_ambience(sr, rng):
    dur = 18.0
    n = int(dur * sr)
    t = np.arange(n) / sr
    # Wind: circular pink-ish noise shaped in the frequency domain (seamless by construction).
    spec = np.fft.rfft(rng.normal(0, 1, n))
    f = np.fft.rfftfreq(n, 1 / sr)
    shape = 1.0 / np.sqrt(np.maximum(f, 20.0)) * np.exp(-f / 900.0)
    shape[f < 40] *= (f[f < 40] / 40.0)
    wind = np.fft.irfft(spec * shape, n)
    wind /= np.max(np.abs(wind))
    # Slow gusts: sum of sines with whole cycles over the loop.
    gust = 0.55 + 0.25 * np.sin(2 * np.pi * 2 * t / dur) + 0.15 * np.sin(2 * np.pi * 5 * t / dur + 1.3) \
        + 0.08 * np.sin(2 * np.pi * 11 * t / dur + 0.4)
    # A second, airier layer that swells against the first.
    spec2 = np.fft.rfft(rng.normal(0, 1, n))
    shape2 = np.exp(-((f - 2200.0) / 1400.0) ** 2)
    air = np.fft.irfft(spec2 * shape2, n)
    air /= np.max(np.abs(air))
    air_gain = 0.3 + 0.2 * np.sin(2 * np.pi * 3 * t / dur + 2.0)
    y = 0.55 * wind * gust + 0.12 * air * air_gain

    # Birdsong: a few "species", placed deterministically, rendered circularly.
    birds = np.zeros(n)

    def chirp(f0, f1, length, vib=0.0, vib_rate=30.0):
        k = int(length * sr)
        tt = np.arange(k) / sr
        freq = sweep(f0, f1, k) * (1 + vib * np.sin(2 * np.pi * vib_rate * tt))
        e = np.sin(np.pi * np.linspace(0, 1, k)) ** 1.5
        return sine(freq, k, sr) * e + 0.15 * sine(freq * 2, k, sr) * e

    def song_a(base):            # warbler: rising-falling trill
        parts = []
        for i in range(6):
            parts.append(chirp(base * (1 + 0.05 * i), base * (1.25 + 0.05 * i), 0.045, 0.02))
            parts.append(np.zeros(int(0.025 * sr)))
        parts.append(chirp(base * 1.5, base * 0.9, 0.18, 0.03, 45))
        return np.concatenate(parts)

    def song_b(base):            # two-note "tee-yoo"
        return np.concatenate((chirp(base * 1.3, base * 1.4, 0.12), np.zeros(int(0.06 * sr)),
                               chirp(base * 1.05, base * 0.85, 0.2, 0.01)))

    def song_c(base):            # quick chips
        parts = []
        for i in range(3):
            parts.append(chirp(base * 1.6, base * 1.1, 0.03))
            parts.append(np.zeros(int(0.07 * sr)))
        return np.concatenate(parts)

    events = [(0.8, song_a, 3100, 0.5), (2.6, song_b, 2600, 0.6), (4.1, song_c, 3600, 0.35),
              (5.9, song_a, 2900, 0.35), (7.4, song_b, 2700, 0.5), (9.3, song_c, 3300, 0.45),
              (10.2, song_a, 3300, 0.55), (12.4, song_b, 2500, 0.4), (13.6, song_c, 3800, 0.3),
              (15.2, song_a, 3000, 0.45), (16.9, song_b, 2800, 0.5), (17.6, song_c, 3500, 0.25)]
    for start, fn, base, g in events:
        place(birds, g * fn(base), int(start * sr), wrap=True)
    birds = echo(birds, sr, 0.13, 0.22, taps=3, circular=True, damp=3000)
    y = y + 0.5 * birds
    return highpass(y, sr, 40, order=1, circular=True)


# --------------------------------------------------------------------------------------------
# Resource documents
# --------------------------------------------------------------------------------------------

SOUNDS = [
    # name, builder, sample rate, peak dBFS, volume, loop, bus, kind
    ("Jump", sfx_jump, SR_SFX, -3.0, 0.8, False, "sfx"),
    ("Coin", sfx_coin, SR_SFX, -3.0, 0.75, False, "sfx"),
    ("Big Coin", sfx_big_coin, SR_SFX, -3.0, 0.8, False, "sfx"),
    ("Stomp", sfx_stomp, SR_SFX, -3.0, 0.85, False, "sfx"),
    ("Bounce", sfx_bounce, SR_SFX, -3.0, 0.8, False, "sfx"),
    ("Block", sfx_block, SR_SFX, -3.0, 0.85, False, "sfx"),
    ("Bump", sfx_bump, SR_SFX, -3.0, 0.8, False, "sfx"),
    ("Hurt", sfx_hurt, SR_SFX, -3.0, 0.85, False, "sfx"),
    ("Checkpoint Chime", sfx_checkpoint, SR_SFX, -3.0, 0.8, False, "sfx"),
    ("Pause", sfx_pause, SR_SFX, -3.0, 0.75, False, "sfx"),
    ("Land", sfx_land, SR_SFX, -3.0, 0.5, False, "sfx"),
    ("Bee Buzz", sfx_bee_buzz, SR_SFX, -3.0, 0.35, True, "sfx"),
    ("Finish Fanfare", sfx_finish, SR_SFX, -3.0, 0.8, False, "sfx"),
    ("Game Over", sfx_game_over, SR_SFX, -3.0, 0.8, False, "sfx"),
    ("Extra Life", sfx_extra_life, SR_SFX, -3.0, 0.8, False, "sfx"),
    ("Meadow Theme", meadow_theme, SR_MUSIC, -6.0, 0.6, True, "music"),
    ("Title Theme", title_theme, SR_MUSIC, -6.0, 0.6, True, "music"),
    ("Meadow Ambience", meadow_ambience, SR_AMB, -9.0, 0.5, True, "ambient"),
]


def guid_for(name):
    return hashlib.md5(("MushroomMeadow/Audio/" + name).encode("utf-8")).hexdigest()


def write_resource(name, loop, bus, volume):
    doc = {
        "source": "Assets/Audio/%s.wav" % name,
        "volume": volume,
        "pitch": 1,
        "loop": loop,
        "spatial": False,
        "audioGroup": "Default Audio Group",
        "bus": bus,
        "environmentRole": "None",
    }
    path = os.path.join(OUT, name + ".audio.json")
    with open(path, "w", encoding="utf-8", newline="\n") as fh:
        fh.write(json.dumps(doc, indent=2) + "\n")

    meta_path = path + ".meta"
    meta = None
    if os.path.exists(meta_path):
        with open(meta_path, "r", encoding="utf-8-sig") as fh:
            meta = json.load(fh)
    if not meta or not meta.get("guid"):
        meta = {"guid": guid_for(name), "kind": "Audio", "displayName": name,
                "createdUtc": "2026-10-11T00:00:00Z"}
    meta["kind"] = "Audio"
    meta["displayName"] = name
    meta["modifiedUtc"] = "2026-10-11T00:00:00Z"
    ordered = {k: meta[k] for k in ("guid", "kind", "displayName", "createdUtc", "modifiedUtc")}
    with open(meta_path, "w", encoding="utf-8", newline="\n") as fh:
        fh.write(json.dumps(ordered, indent=2) + "\n")


def main():
    os.makedirs(OUT, exist_ok=True)
    for i, (name, fn, sr, peak, volume, loop, bus) in enumerate(SOUNDS):
        rng = np.random.default_rng(1000 + i)
        y = fn(sr, rng)
        if not loop:
            # Trim the silent echo tail (below -60 dB of peak), then fade the last 10 ms.
            loud = np.nonzero(np.abs(y) > np.max(np.abs(y)) * 0.001)[0]
            y = fade_tail(y[: min(len(y), loud[-1] + int(0.02 * sr))].copy(), sr, 0.01)
            k = int(0.0015 * sr)
            y[:k] *= np.linspace(0.0, 1.0, k)
        path = write_wav(name, y, sr, peak, loop)
        write_resource(name, loop, bus, volume)
        print("%-16s %6.2f s  %5d Hz  %8d bytes  bus=%s loop=%s" %
              (name, len(y) / sr, sr, os.path.getsize(path), bus, loop))


if __name__ == "__main__":
    main()
