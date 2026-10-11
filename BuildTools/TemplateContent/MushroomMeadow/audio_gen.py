"""Mushroom Meadow template audio generator (SNES / chiptune style).

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
# Sound effects
# --------------------------------------------------------------------------------------------

def sfx_jump(sr, rng):
    n = int(0.24 * sr)
    f = sweep(260, 780, n) * (1 + 0.02 * np.sin(np.linspace(0, 12, n)))
    y = pulse(f, n, sr, 0.25) * env_adsr(n, sr, 0.003, 0.09, 0.35, 0.06)
    y = lowpass(y, sr, 7000)
    return echo(fade_tail(y, sr), sr, 0.06, 0.25, taps=2)


def sfx_coin(sr, rng):
    a = tone("pulse", nhz("B5"), 0.07, sr, 0.5, env_adsr(int(0.07 * sr), sr, 0.002, 0.05, 0.8, 0.005))
    nb = int(0.42 * sr)
    b = tone("pulse", nhz("E6"), 0.42, sr, 0.5, env_exp(nb, sr, 0.13))
    b += 0.3 * tone("pulse", nhz("E7"), 0.42, sr, 0.25, env_exp(nb, sr, 0.06))
    y = lowpass(np.concatenate((a, b)), sr, 9000)
    return echo(fade_tail(y, sr), sr, 0.085, 0.3, taps=3)


def sfx_big_coin(sr, rng):
    notes = ["C6", "E6", "G6", "C7", "E6", "G6", "C7", "E7"]
    parts = []
    for i, nm in enumerate(notes):
        n = int(0.045 * sr)
        parts.append(tone("pulse", nhz(nm), 0.045, sr, 0.5 if i % 2 else 0.25,
                          env_adsr(n, sr, 0.002, 0.03, 0.7, 0.006)))
    n = int(0.75 * sr)
    t = np.arange(n) / sr
    trill = np.where((t * 28).astype(int) % 2 == 0, nhz("C7"), nhz("G6"))
    shimmer = pulse(trill, n, sr, 0.25) * env_exp(n, sr, 0.25)
    shimmer += 0.5 * triangle(nhz("C6"), n, sr) * env_exp(n, sr, 0.3)
    y = lowpass(np.concatenate(parts + [shimmer]), sr, 9500)
    return echo(fade_tail(y, sr), sr, 0.11, 0.35, taps=4)


def sfx_stomp(sr, rng):
    n = int(0.11 * sr)
    a = pulse(sweep(620, 140, n), n, sr, 0.5) * env_exp(n, sr, 0.06)
    nz = lowpass(noise(n, rng, 2), sr, 4000) * env_exp(n, sr, 0.025)
    n2 = int(0.09 * sr)
    b = pulse(sweep(900, 1300, n2), n2, sr, 0.25) * env_exp(n2, sr, 0.04)
    y = np.concatenate((a + 0.8 * nz, np.zeros(int(0.015 * sr)), 0.6 * b))
    return fade_tail(lowpass(y, sr, 8000), sr)


def sfx_bounce(sr, rng):
    n = int(0.5 * sr)
    t = np.arange(n) / sr
    base = sweep(180, 520, n)
    wob = 1 + 0.12 * np.sin(2 * np.pi * 14 * t) * np.exp(-t / 0.25)
    y = triangle(base * wob, n, sr) * env_adsr(n, sr, 0.003, 0.2, 0.25, 0.12)
    y += 0.35 * pulse(base * wob * 2, n, sr, 0.125) * env_exp(n, sr, 0.12)
    return echo(fade_tail(lowpass(y, sr, 6000), sr), sr, 0.07, 0.2, taps=2)


def sfx_block(sr, rng):
    n = int(0.38 * sr)
    crunch = noise(n, rng, 3)
    crunch = lowpass(crunch, sr, 3500) * env_exp(n, sr, 0.07)
    m = int(0.05 * sr)
    bits = []
    for f in (520, 440, 370, 300):
        bits.append(pulse(sweep(f, f * 0.7, m), m, sr, 0.25) * env_exp(m, sr, 0.03))
    debris = np.zeros(n)
    place(debris, np.concatenate(bits), int(0.06 * sr))
    k = int(0.06 * sr)
    thud = triangle(sweep(160, 70, k), k, sr) * env_exp(k, sr, 0.04)
    y = crunch + 0.6 * debris
    y[:k] += thud
    return fade_tail(y, sr)


def sfx_bump(sr, rng):
    n = int(0.16 * sr)
    y = triangle(sweep(150, 85, n), n, sr) * env_exp(n, sr, 0.06)
    y += 0.5 * pulse(sweep(220, 110, n), n, sr, 0.5) * env_exp(n, sr, 0.03)
    y += 0.3 * lowpass(noise(n, rng, 4), sr, 1500) * env_exp(n, sr, 0.02)
    return fade_tail(lowpass(y, sr, 4000), sr)


def sfx_hurt(sr, rng):
    n = int(0.5 * sr)
    t = np.arange(n) / sr
    f = sweep(820, 190, n) * (1 + 0.06 * np.sin(2 * np.pi * 22 * t))
    y = pulse(f, n, sr, 0.5) * env_adsr(n, sr, 0.003, 0.15, 0.5, 0.15)
    y *= 0.75 + 0.25 * np.sign(np.sin(2 * np.pi * 30 * t))
    y += 0.25 * lowpass(noise(n, rng, 6), sr, 3000) * env_exp(n, sr, 0.05)
    return echo(fade_tail(lowpass(y, sr, 6500), sr), sr, 0.05, 0.2, taps=2)


def sfx_checkpoint(sr, rng):
    parts = []
    for nm in ("G5", "C6", "E6", "G6"):
        n = int(0.08 * sr)
        parts.append(tone("pulse", nhz(nm), 0.08, sr, 0.25, env_adsr(n, sr, 0.002, 0.05, 0.6, 0.01)))
    n = int(0.8 * sr)
    chord = np.zeros(n)
    for nm, g in (("C6", 0.6), ("E6", 0.5), ("G6", 0.5), ("C7", 0.4)):
        chord += g * tone("pulse", nhz(nm), 0.8, sr, 0.5, env_exp(n, sr, 0.3), vibrato=0.15, vib_delay=0.1)
    chord += 0.6 * triangle(nhz("C4"), n, sr) * env_exp(n, sr, 0.35)
    y = lowpass(np.concatenate(parts + [chord]), sr, 8000)
    return echo(fade_tail(y, sr), sr, 0.12, 0.35, taps=4)


def sfx_pause(sr, rng):
    out = []
    for nm, dur in (("E6", 0.05), ("C6", 0.05), ("E6", 0.05), ("C6", 0.05), ("E6", 0.12)):
        n = int(dur * sr)
        out.append(tone("pulse", nhz(nm), dur, sr, 0.5, env_adsr(n, sr, 0.002, 0.04, 0.7, 0.008)))
        out.append(np.zeros(int(0.02 * sr)))
    y = lowpass(np.concatenate(out), sr, 8000)
    return echo(fade_tail(y, sr), sr, 0.07, 0.25, taps=2)


def sfx_land(sr, rng):
    n = int(0.1 * sr)
    y = lowpass(noise(n, rng, 2), sr, 1200) * env_exp(n, sr, 0.025)
    y += 0.6 * triangle(sweep(120, 60, n), n, sr) * env_exp(n, sr, 0.03)
    return fade_tail(y, sr)


def sfx_bee_buzz(sr, rng):
    # Seamless 0.5 s loop: every frequency completes a whole number of cycles in the loop.
    dur = 0.5
    n = int(dur * sr)
    t = np.arange(n) / sr
    f0, fm, ftrem = 176.0, 26.0, 8.0          # 88, 13 and 4 cycles in 0.5 s
    ph = f0 * t + 0.35 * np.sin(2 * np.pi * fm * t)
    sq = np.where(ph % 1.0 < 0.3, 1.0, -1.0)
    saw = 2.0 * (ph % 1.0) - 1.0
    y = 0.6 * sq + 0.4 * saw
    y *= 0.75 + 0.25 * np.sin(2 * np.pi * ftrem * t)
    return lowpass(y, sr, 2600, circular=True)


def render_seq(seq, sr, bpm, kind="pulse", duty=0.5, gain=1.0, decay=None, vibrato=0.0, legato=0.9):
    """seq: list of (note or None, beats). Returns mono array."""
    beat = 60.0 / bpm
    total = sum(b for _, b in seq) * beat
    buf = np.zeros(int(total * sr) + int(0.6 * sr))
    pos = 0.0
    for nm, b in seq:
        dur = b * beat
        if nm:
            n = int(dur * legato * sr)
            env = env_exp(n, sr, decay) if decay else env_adsr(n, sr, 0.004, 0.1, 0.65, min(0.04, dur * 0.3))
            env = fade_tail(env.copy(), sr, 0.004)
            place(buf, gain * tone(kind, nhz(nm), dur * legato, sr, duty, env, vibrato=vibrato), int(pos * sr))
        pos += dur
    return buf


def sfx_finish(sr, rng):
    bpm = 160
    lead = [("G4", .25), ("C5", .25), ("E5", .25), ("G5", .25), ("C6", .25), ("E6", .25), ("G6", 1.0),
            ("E6", .75), ("Ab5", .25), ("C6", .25), ("Eb6", .25), ("Ab6", 1.0), ("F6", .75),
            ("Bb5", .25), ("D6", .25), ("F6", .25), ("Bb6", 1.0), ("Bb6", .33), ("Bb6", .33), ("Bb6", .34),
            ("C7", 2.5)]
    harm = [(None, 1.5), ("C6", 1.0), ("G5", .75), (None, .75), ("Eb6", 1.0), ("C6", .75),
            (None, .75), ("F6", 1.0), ("F6", .33), ("F6", .33), ("F6", .34), ("G6", 2.5)]
    bass = [("C3", 1.5), ("C3", 1.0), ("C3", .75), ("Ab2", .75), ("Ab2", 1.0), ("Ab2", .75),
            ("Bb2", .75), ("Bb2", 1.0), ("Bb2", 1.0), ("C3", 2.5)]
    a = render_seq(lead, sr, bpm, "pulse", 0.5, 0.55, vibrato=0.2)
    b = render_seq(harm, sr, bpm, "pulse", 0.25, 0.3, vibrato=0.2)
    c = render_seq(bass, sr, bpm, "tri", gain=0.8, legato=0.95)
    n = max(len(a), len(b), len(c))
    y = np.zeros(n)
    for part in (a, b, c):
        y[:len(part)] += part
    # Snare roll under the final triplet into the last chord.
    beat = 60.0 / bpm
    for k in range(6):
        m = int(0.06 * sr)
        hit = highpass(noise(m, rng), sr, 1500) * env_exp(m, sr, 0.03) * (0.15 + 0.03 * k)
        place(y, hit, int((10.5 + k * 0.166) * beat * sr))
    y = lowpass(y, sr, 9000)
    return echo(fade_tail(y, sr, 0.05), sr, 0.14, 0.3, taps=4)


def sfx_game_over(sr, rng):
    bpm = 96
    lead = [("C5", .5), (None, .25), ("G4", .5), (None, .25), ("E4", .5),
            ("A4", .33), ("B4", .33), ("A4", .34), ("Ab4", .33), ("Bb4", .33), ("Ab4", .34),
            ("G4", .25), ("F4", .25), ("G4", 2.0)]
    bass = [("C3", .75), ("G2", .75), ("E2", .5), ("F2", 1.0), ("Db3", 1.0), ("C3", 2.5)]
    a = render_seq(lead, sr, bpm, "pulse", 0.5, 0.6, vibrato=0.25)
    b = render_seq(bass, sr, bpm, "tri", gain=0.8, legato=0.95)
    n = max(len(a), len(b))
    y = np.zeros(n)
    y[:len(a)] += a
    y[:len(b)] += b
    y = lowpass(y, sr, 7000)
    return echo(fade_tail(y, sr, 0.05), sr, 0.16, 0.3, taps=4)


def sfx_extra_life(sr, rng):
    notes = ["E6", "A6", "C#7", "E7", "A7", "E7", "A7"]
    lens = [0.07, 0.07, 0.07, 0.07, 0.07, 0.07, 0.3]
    parts = []
    for nm, d in zip(notes, lens):
        n = int(d * sr)
        e = env_adsr(n, sr, 0.002, 0.05, 0.7, 0.01) if d < 0.2 else env_exp(n, sr, 0.12)
        parts.append(tone("pulse", nhz(nm), d, sr, 0.25, e))
    y = np.concatenate(parts)
    n = len(y)
    y += 0.45 * triangle(nhz("A4"), n, sr) * env_exp(n, sr, 0.3)
    y = lowpass(y, sr, 9500)
    return echo(fade_tail(y, sr), sr, 0.09, 0.35, taps=4)


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
