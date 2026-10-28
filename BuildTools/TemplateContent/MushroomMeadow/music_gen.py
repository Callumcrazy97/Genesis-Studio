"""Mushroom Meadow (full game) music + ambience generator.

Deterministic: re-running produces byte-identical WAVs. Writes every track into
Source/Genesis.Application.Core/Projects/Templates/Assets/TwoDShowcase/Assets/Audio
with its .audio.json resource document and .audio.json.meta (kind "Audio").

Every piece is rendered *circularly*: notes, echoes and reverb tails that run past the end
of the loop wrap round to its start, and every filter is run on two copies of the loop
so it has settled when the loop restarts. The seams are therefore continuous by construction.

A shared "Glowspore motif" (sol-do-re-mi, sol-mi-re-do: MOTIF below) recurs in every track,
re-harmonised, re-voiced and re-timed for each world.

Reuses the phase-1 synth helpers from audio_gen.py (imported, never modified); phase-1
outputs are not touched.

Usage:  python BuildTools/TemplateContent/MushroomMeadow/music_gen.py            (render all)
        python BuildTools/TemplateContent/MushroomMeadow/music_gen.py --verify   (check files)
        python BuildTools/TemplateContent/MushroomMeadow/music_gen.py "Cavern Theme" ...
Requires numpy + scipy.
"""

import hashlib
import json
import os
import re
import sys
import wave

import numpy as np
from scipy.ndimage import maximum_filter1d, minimum_filter1d, uniform_filter1d
from scipy.signal import lfilter

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import audio_gen as ag  # noqa: E402  (phase-1 generator: synth helpers + resource writer)
from audio_gen import (OUT, bandpass, highpass, hz, lowpass, midi, phase_of, pulse,  # noqa: E402
                       sine, triangle, _blep)

SR = 22050
MAJOR = (0, 2, 4, 5, 7, 9, 11)
MINOR = (0, 2, 3, 5, 7, 8, 10)

# The Glowspore motif in scale degrees (0 = tonic): "sol do re mi-- sol | mi re do-----" (2 bars of 4/4).
MOTIF = "-3:.5 0:.5 1:.5 2:1.5 4:1  2:.5 1:.5 0:3"

# --------------------------------------------------------------------------------------------
# Small DSP helpers
# --------------------------------------------------------------------------------------------


def saw(freq, n, sr):
    ph, dt = phase_of(freq, n, sr)
    dt = np.clip(dt, 1e-6, 0.5)
    t = ph % 1.0
    return 2.0 * t - 1.0 - _blep(t, dt)


def adsr(dur, sr, a=0.01, d=0.1, s=0.7, r=0.1):
    """Envelope for a note held `dur` seconds, then released over `r` seconds."""
    r = max(r, 0.005)
    n = max(8, int((dur + r) * sr))
    t = np.arange(n) / sr
    a = max(a, 0.0005)
    e = np.where(t < a, t / a, s + (1.0 - s) * np.exp(-np.maximum(t - a, 0.0) / max(d, 1e-4)))
    return e * np.clip(1.0 - (t - dur) / r, 0.0, 1.0)


def vib_freq(f, n, sr, depth=0.0, rate=5.0, delay=0.2, phase=0.0):
    if depth <= 0:
        return np.full(n, float(f))
    t = np.arange(n) / sr
    amt = np.clip((t - delay) / 0.3, 0.0, 1.0) * depth
    return f * 2.0 ** (amt * np.sin(2 * np.pi * rate * t + phase) / 12.0)


def onepole(x, coef):
    return lfilter([1.0 - coef], [1.0, -coef], x)


def place_wrap(buf, x, start):
    """Add x into the circular buffer at sample `start` (wrapping, any length)."""
    n = len(buf)
    start = int(start) % n
    pos = 0
    while pos < len(x):
        k = min(n - start, len(x) - pos)
        buf[start:start + k] += x[pos:pos + k]
        pos += k
        start = 0


def conv_circular(x, ir):
    n = len(x)
    if len(ir) > n:
        ir = ir[:n]
    return np.fft.irfft(np.fft.rfft(x) * np.fft.rfft(ir, n), n)


def reverb_ir(sr, seconds, rng, damp=5000.0, predelay=0.02, early=True):
    n = int(seconds * sr)
    t = np.arange(n) / sr
    tail = rng.normal(0.0, 1.0, n) * np.exp(-6.9 * t / seconds)
    # Frequency-dependent decay: blend a bright early tail into a darker late one.
    dark = lowpass(tail, sr, damp * 0.35, order=1)
    bright = lowpass(tail, sr, damp, order=1)
    w = np.exp(-t / (seconds * 0.25))
    tail = bright * w + dark * (1 - w)
    tail *= np.minimum(1.0, t / 0.03)
    ir = np.zeros(n + int(predelay * sr))
    ir[int(predelay * sr):] = tail
    if early:
        for k, (dt, g) in enumerate(((0.011, 0.5), (0.019, -0.4), (0.027, 0.35), (0.041, -0.3), (0.053, 0.25))):
            ir[int((predelay + dt) * sr)] += g * 6
    return ir / np.sqrt(np.sum(ir ** 2))


def echo_ir(sr, delay, feedback, taps, damp=4000.0):
    d = int(delay * sr)
    ir = np.zeros(d * taps + 1)
    for k in range(1, taps + 1):
        ir[d * k] = feedback ** k
    return lowpass(ir, sr, damp, order=1)


def chorus_circular(x, sr, base=0.012, depth=0.004, rate=0.8, loop_seconds=None):
    n = len(x)
    if loop_seconds:
        rate = max(1, round(rate * loop_seconds)) / loop_seconds
    t = np.arange(n) / sr
    out = np.zeros(n)
    for ph in (0.0, 2.1):
        d = (base + depth * np.sin(2 * np.pi * rate * t + ph)) * sr
        idx = np.arange(n) - d
        i0 = np.floor(idx).astype(np.int64)
        fr = idx - i0
        out += x[i0 % n] * (1 - fr) + x[(i0 + 1) % n] * fr
    return out * 0.5


def lp_c(x, fc, order=2):
    return lowpass(x, SR, fc, order=order, circular=True)


def hp_c(x, fc, order=2):
    return highpass(x, SR, fc, order=order, circular=True)


def circ_noise(n, rng, shape_fn):
    """Seamless noise: shape white noise in the frequency domain of the whole loop."""
    spec = np.fft.rfft(rng.normal(0.0, 1.0, n))
    f = np.fft.rfftfreq(n, 1.0 / SR)
    y = np.fft.irfft(spec * shape_fn(f), n)
    return y / (np.max(np.abs(y)) + 1e-12)


def loop_lfo(t, total, cycles, phase=0.0):
    return np.sin(2 * np.pi * cycles * t / total + phase)


# --------------------------------------------------------------------------------------------
# Instruments (each returns the whole sounding note including its release / ring)
# --------------------------------------------------------------------------------------------


def v_pulse(f, dur, sr, rng, duty=0.5, a=0.005, d=0.12, s=0.6, r=0.05, vib=0.0, lp=None):
    env = adsr(dur, sr, a, d, s, r)
    n = len(env)
    y = pulse(vib_freq(f, n, sr, vib, 5.5, 0.15), n, sr, duty) * env
    return lowpass(y, sr, lp) if lp else y


def v_tri(f, dur, sr, rng, a=0.004, d=0.15, s=0.8, r=0.04, vib=0.0):
    env = adsr(dur, sr, a, d, s, r)
    n = len(env)
    return triangle(vib_freq(f, n, sr, vib), n, sr) * env


def v_sine(f, dur, sr, rng, a=0.01, d=0.2, s=0.8, r=0.08, vib=0.0, h2=0.0):
    env = adsr(dur, sr, a, d, s, r)
    n = len(env)
    ph, _ = phase_of(vib_freq(f, n, sr, vib, 5.0, 0.2), n, sr)
    y = np.sin(2 * np.pi * ph)
    if h2:
        y += h2 * np.sin(4 * np.pi * ph)
    return y * env


def v_strings(f, dur, sr, rng, a=0.25, d=0.4, s=0.9, r=0.35, vib=0.12, lp=2600.0, tremolo=0.0):
    env = adsr(dur, sr, a, d, s, r)
    n = len(env)
    y = np.zeros(n)
    for k, dt in enumerate((-0.07, 0.0, 0.08)):
        fk = vib_freq(f * 2 ** (dt / 12.0), n, sr, vib, 4.8 + 0.45 * k, 0.12, phase=k * 2.1)
        y += saw(fk, n, sr)
    y = lowpass(y / 3.0, sr, min(lp, 0.45 * sr))
    if tremolo:
        t = np.arange(n) / sr
        y *= 1.0 - tremolo * (0.5 + 0.5 * np.sin(2 * np.pi * 12.0 * t))
    return y * env


def v_brass(f, dur, sr, rng, a=0.03, r=0.12, bright=3600.0, dark=650.0, vib=0.1, s=0.75):
    env = adsr(dur, sr, a, 0.3, s, r)
    n = len(env)
    t = np.arange(n) / sr
    fr = vib_freq(f, n, sr, vib, 5.2, 0.25) * 2 ** (-0.3 * np.exp(-t / 0.035) / 12.0)
    raw = saw(fr, n, sr) + 0.6 * saw(fr * 1.004, n, sr)
    b = lowpass(raw, sr, min(bright, 0.45 * sr))
    dk = lowpass(raw, sr, dark)
    fe = 0.35 + 0.65 * np.exp(-t / 0.22)
    return (dk + (b - dk) * fe) * env * 0.6


def v_flute(f, dur, sr, rng, a=0.06, r=0.12, vib=0.2, breath=0.08, s=0.85):
    env = adsr(dur, sr, a, 0.25, s, r)
    n = len(env)
    ph, _ = phase_of(vib_freq(f, n, sr, vib, 5.2, 0.18), n, sr)
    y = np.sin(2 * np.pi * ph) + 0.16 * np.sin(4 * np.pi * ph) + 0.05 * np.sin(6 * np.pi * ph)
    nz = bandpass(rng.normal(0, 1, n), sr, min(f * 1.5, 7000), min(f * 4.0, 10000))
    t = np.arange(n) / sr
    puff = 1.0 + 2.0 * np.exp(-t / 0.05)
    return (y + breath * nz * puff) * env


BELLS = {
    "celesta": ((1.0, 1.0, 1.0), (2.0, 0.35, 0.45), (3.0, 0.12, 0.2), (4.0, 0.06, 0.1)),
    "glock": ((1.0, 1.0, 0.9), (2.76, 0.35, 0.3), (5.40, 0.18, 0.12), (8.93, 0.08, 0.05)),
    "musicbox": ((1.0, 1.0, 0.8), (3.0, 0.22, 0.25), (5.95, 0.1, 0.1), (9.2, 0.04, 0.05)),
    "vibes": ((1.0, 1.0, 1.6), (4.0, 0.22, 0.4), (10.0, 0.04, 0.08)),
    "marimba": ((1.0, 1.0, 0.45), (3.93, 0.32, 0.07), (9.9, 0.05, 0.025)),
    "kalimba": ((1.0, 1.0, 0.7), (5.4, 0.18, 0.08), (11.1, 0.05, 0.03)),
    "glass": ((1.0, 1.0, 2.2), (2.32, 0.3, 1.0), (4.25, 0.12, 0.5), (6.63, 0.05, 0.25)),
}


def v_bell(f, dur, sr, rng, kind="celesta", decay=1.0, tremolo=0.0, damp=None, detune=0.0):
    parts = BELLS[kind]
    length = 4.0 * decay * parts[0][2]
    if damp is not None:
        length = min(length, dur + damp)
    n = max(16, int(length * sr))
    t = np.arange(n) / sr
    y = np.zeros(n)
    for ratio, amp, tau in parts:
        fk = f * ratio
        if fk > 0.45 * sr:
            continue
        y += amp * np.sin(2 * np.pi * fk * t + ratio) * np.exp(-t / (tau * decay))
        if detune:
            y += 0.5 * amp * np.sin(2 * np.pi * fk * 2 ** (detune / 1200.0) * t) * np.exp(-t / (tau * decay))
    y *= np.minimum(1.0, t / 0.0015)
    if tremolo:
        y *= 1.0 - tremolo * (0.5 - 0.5 * np.cos(2 * np.pi * 5.5 * t))
    if damp is not None:
        y *= np.clip(1.0 - (t - dur) / damp, 0.0, 1.0)
    return y


def v_piano(f, dur, sr, rng, decay=1.6, r=0.3, bright=1.0):
    n = int((dur + r + 0.02) * sr)
    t = np.arange(n) / sr
    y = np.zeros(n)
    base_tau = decay * (262.0 / f) ** 0.35
    for k in range(1, 11):
        fk = f * k * np.sqrt(1.0 + 0.00035 * k * k)
        if fk > 0.45 * sr:
            break
        amp = (1.0 / k ** 1.25) * (bright ** (k - 1))
        tau = base_tau / k ** 0.6
        y += amp * np.sin(2 * np.pi * fk * t + 0.3 * k) * np.exp(-t / tau)
        if k == 1:
            y += 0.5 * amp * np.sin(2 * np.pi * fk * 1.0011 * t) * np.exp(-t / tau)
    m = int(0.012 * sr)
    y[:m] += lowpass(rng.uniform(-1, 1, m), sr, min(4000.0, f * 6)) * np.exp(-np.arange(m) / (0.003 * sr)) * 0.3
    y *= np.minimum(1.0, t / 0.002) * np.clip(1.0 - (t - dur) / r, 0.0, 1.0)
    return y * 0.7


def v_pluck(f, dur, sr, rng, t60=1.5, bright=0.6, r=0.08, ring=True):
    """Karplus-Strong string (harp, guitar, lute, pizzicato)."""
    total = dur + (min(t60, 2.5) if ring else r)
    # The two-point average adds half a sample of delay, so the loop rings at sr / (period + 0.5).
    # Render at that pitch, then resample to the exact frequency (keeps high notes in tune).
    period = max(2, int(round(sr / f - 0.5)))
    ratio = (sr / (period + 0.5)) / f
    n = int(total * sr / ratio) + 2
    g = 0.001 ** (1.0 / (t60 * f))
    buf = np.zeros(n + period + 2)
    exc = rng.uniform(-1, 1, period)
    exc = onepole(exc, 1.0 - bright)
    buf[1:period + 1] = exc - np.mean(exc)
    pos = period + 1
    while pos < len(buf):
        k = min(period, len(buf) - pos)
        buf[pos:pos + k] = g * 0.5 * (buf[pos - period:pos - period + k] + buf[pos - period - 1:pos - period - 1 + k])
        pos += k
    y = buf[1:n + 1]
    n = int(total * sr)
    y = np.interp(np.arange(n) / ratio, np.arange(len(y)), y)
    if not ring:
        t = np.arange(n) / sr
        y = y * np.clip(1.0 - (t - dur) / r, 0.0, 1.0)
    else:
        y = ag.fade_tail(y.copy(), sr, 0.05)
    return y


def v_choir(f, dur, sr, rng, vowel="ah", a=0.35, r=0.5, vib=0.15, s=0.9):
    env = adsr(dur, sr, a, 0.4, s, r)
    n = len(env)
    src = np.zeros(n)
    for k, dt in enumerate((-0.1, 0.0, 0.09)):
        fk = vib_freq(f * 2 ** (dt / 12.0), n, sr, vib, 4.6 + 0.5 * k, 0.1, phase=k * 1.7)
        src += saw(fk, n, sr)
    form = {"ah": ((780, 1.0, 90), (1150, 0.5, 110), (2800, 0.12, 150)),
            "oo": ((340, 1.0, 70), (820, 0.35, 90), (2400, 0.05, 120)),
            "oh": ((520, 1.0, 80), (900, 0.45, 100), (2600, 0.08, 130))}[vowel]
    y = np.zeros(n)
    for fc, g, bw in form:
        y += g * bandpass(src, sr, fc - bw, fc + bw)
    y += 0.02 * highpass(rng.normal(0, 1, n), sr, 3000)
    return y * env * 1.6


def v_organ(f, dur, sr, rng, a=0.015, r=0.1, leslie=0.15):
    env = adsr(dur, sr, a, 0.1, 1.0, r)
    n = len(env)
    t = np.arange(n) / sr
    y = np.zeros(n)
    for k, g in ((0.5, 0.5), (1, 1.0), (2, 0.55), (3, 0.3), (4, 0.25), (6, 0.1), (8, 0.08)):
        if f * k < 0.45 * sr:
            y += g * np.sin(2 * np.pi * f * k * t + k)
    y *= 1.0 - leslie * (0.5 + 0.5 * np.sin(2 * np.pi * 6.2 * t))
    return y * env * 0.45


def v_accordion(f, dur, sr, rng, a=0.025, r=0.08, s=0.9):
    env = adsr(dur, sr, a, 0.15, s, r)
    n = len(env)
    y = pulse(f * 1.0035, n, sr, 0.32) + pulse(f / 1.0035, n, sr, 0.32) + 0.45 * pulse(f * 2, n, sr, 0.5)
    t = np.arange(n) / sr
    y *= 1.0 + 0.08 * np.sin(2 * np.pi * 5.0 * t)
    return lowpass(y, sr, 2800) * env * 0.4


def v_saw_bass(f, dur, sr, rng, a=0.004, r=0.04, cutoff=900.0, s=0.8):
    env = adsr(dur, sr, a, 0.12, s, r)
    n = len(env)
    t = np.arange(n) / sr
    raw = saw(np.full(n, f), n, sr)
    b = lowpass(raw, sr, cutoff * 2.5)
    dk = lowpass(raw, sr, cutoff * 0.5)
    fe = np.exp(-t / 0.08)
    y = dk + (b - dk) * fe + 0.6 * np.sin(2 * np.pi * f * t)
    return y * env * 0.7


VOICES = {"pulse": v_pulse, "tri": v_tri, "sine": v_sine, "strings": v_strings, "brass": v_brass,
          "flute": v_flute, "bell": v_bell, "piano": v_piano, "pluck": v_pluck, "choir": v_choir,
          "organ": v_organ, "accordion": v_accordion, "sawbass": v_saw_bass}

# --------------------------------------------------------------------------------------------
# Percussion
# --------------------------------------------------------------------------------------------


def make_kit(sr, rng):
    kit = ag.drum_kit(sr, rng)
    n = int(0.9 * sr)
    t = np.arange(n) / sr
    crash = highpass(rng.uniform(-1, 1, n), sr, 3200) * np.exp(-t / 0.45)
    crash += 0.4 * bandpass(rng.uniform(-1, 1, n), sr, 5000, 9000) * np.exp(-t / 0.7)
    kit["crash"] = ag.fade_tail(crash * 0.55, sr, 0.1)
    kit["swell"] = (kit["crash"][::-1] * np.linspace(0.2, 1.0, n) ** 2).copy()
    n = int(0.12 * sr)
    t = np.arange(n) / sr
    tamb = highpass(rng.uniform(-1, 1, n), sr, 6500) * np.exp(-t / 0.035)
    tamb += 0.5 * bandpass(rng.uniform(-1, 1, n), sr, 7500, 10000) * np.exp(-t / 0.07)
    kit["tamb"] = tamb * 0.6
    n = int(0.08 * sr)
    t = np.arange(n) / sr
    kit["wood"] = (np.sin(2 * np.pi * 1050 * t) + 0.4 * np.sin(2 * np.pi * 2380 * t)) * np.exp(-t / 0.018)
    n = int(0.2 * sr)
    t = np.arange(n) / sr
    clap = np.zeros(n)
    for k, off in enumerate((0.0, 0.011, 0.022)):
        m = int(off * sr)
        burst = bandpass(rng.uniform(-1, 1, n - m), sr, 900, 3200) * np.exp(-np.arange(n - m) / sr / (0.012 if k < 2 else 0.06))
        clap[m:] += burst
    kit["clap"] = clap * 0.7
    n = int(0.6 * sr)
    t = np.arange(n) / sr
    kit["brush"] = bandpass(rng.uniform(-1, 1, n), sr, 2000, 8000) * np.minimum(1, t / 0.03) * np.exp(-t / 0.09) * 0.5
    kit["rim"] = (np.sin(2 * np.pi * 1700 * t[: int(0.05 * sr)]) * np.exp(-t[: int(0.05 * sr)] / 0.008)) * 0.6
    return kit


def timpani(m, sr, rng, decay=0.9, gain=1.0):
    f = hz(m)
    n = int(decay * 3.5 * sr)
    t = np.arange(n) / sr
    fr = f * (1.0 + 0.03 * np.exp(-t / 0.08))
    ph = np.cumsum(fr) / sr
    y = np.sin(2 * np.pi * ph) * np.exp(-t / decay)
    y += 0.5 * np.sin(2 * np.pi * 1.5 * ph) * np.exp(-t / (decay * 0.5))
    y += 0.25 * np.sin(2 * np.pi * 1.99 * ph) * np.exp(-t / (decay * 0.35))
    m2 = int(0.02 * sr)
    y[:m2] += lowpass(rng.uniform(-1, 1, m2), sr, 1500) * np.linspace(1, 0, m2) * 0.6
    return ag.fade_tail(y * gain, sr, 0.05)


def tom(m, sr, rng, decay=0.22, gain=1.0):
    f = hz(m)
    n = int(decay * 4 * sr)
    t = np.arange(n) / sr
    ph = np.cumsum(f * (1 + 0.5 * np.exp(-t / 0.03))) / sr
    y = np.sin(2 * np.pi * ph) * np.exp(-t / decay)
    m2 = int(0.01 * sr)
    y[:m2] += rng.uniform(-1, 1, m2) * 0.3
    return ag.fade_tail(y * gain, sr, 0.02)


def gong(m, sr, rng, decay=4.0, gain=1.0):
    f = hz(m)
    n = int(decay * 2.5 * sr)
    t = np.arange(n) / sr
    y = np.zeros(n)
    for ratio, amp, tk in ((1.0, 1.0, 1.0), (1.47, 0.6, 0.8), (2.09, 0.5, 0.6), (2.56, 0.35, 0.5),
                           (3.18, 0.25, 0.4), (4.3, 0.15, 0.3)):
        bloom = 1.0 - np.exp(-t / (0.05 + 0.15 * ratio))
        y += amp * np.sin(2 * np.pi * f * ratio * t + ratio * 3) * np.exp(-t / (decay * tk)) * bloom
    return ag.fade_tail(y * gain * 0.5, sr, 0.2)


# --------------------------------------------------------------------------------------------
# Music theory helpers
# --------------------------------------------------------------------------------------------


def parse_deg(tok):
    acc = 0
    while tok and tok[-1] in "#b" and len(tok) > 1:
        acc += 1 if tok[-1] == "#" else -1
        tok = tok[:-1]
    return int(tok), acc


class Chord:
    def __init__(self, root, ivals, bass):
        self.root = root
        self.ivals = ivals
        self.bass = bass
        self.pcs = {(root + i) % 12 for i in ivals}

    def notes(self, lo, hi):
        return [m for m in range(lo, hi + 1) if m % 12 in self.pcs]

    def close(self, center, count=3):
        return self.notes(center, center + 24)[:count]

    def fifth(self):
        return self.root + (self.ivals[2] if len(self.ivals) > 2 else 7)

    def third(self):
        return self.root + self.ivals[1]


CHORD_RE = re.compile(r"^(b|#)?(\d)([Mmd7s]*)(?:/(-?\d+[#b]?))?$")


class Key:
    def __init__(self, tonic, scale):
        self.tonic = midi(tonic)
        self.scale = scale

    def deg(self, d, acc=0, octave=0):
        o, i = divmod(d, 7)
        return self.tonic + 12 * (o + octave) + self.scale[i] + acc

    def chord(self, token, octave=-2):
        mt = CHORD_RE.match(token)
        if not mt:
            raise ValueError("bad chord " + token)
        pre, r, flags, slash = mt.groups()
        r = int(r)
        acc = -1 if pre == "b" else (1 if pre == "#" else 0)
        root = self.deg(r, acc, octave)
        base = self.deg(r, 0, octave)
        third = self.deg(r + 2, 0, octave) - base
        fifth = self.deg(r + 4, 0, octave) - base
        seventh = self.deg(r + 6, 0, octave) - base
        if "M" in flags:
            third, fifth = 4, 7
        if "m" in flags:
            third, fifth = 3, 7
        if "d" in flags:
            third, fifth = 3, 6
        if "s" in flags:
            third = 5
        ivals = [0, third, fifth]
        if "7" in flags:
            ivals.append(10 if ("M" in flags or "m" in flags) else seventh)
        bass = root
        if slash:
            bd, bacc = parse_deg(slash)
            bass = self.deg(bd, bacc, octave)
            if bass > root:
                bass -= 12
        return Chord(root, ivals, bass)

    def chords(self, text, octave=-2):
        return [self.chord(tok, octave) for tok in text.split()]


def shift_motif(text, steps):
    """Diatonic transposition of a melody string."""
    out = []
    for tok in text.split():
        d, b = tok.split(":")
        if d == "r":
            out.append(tok)
            continue
        deg, acc = parse_deg(d)
        suffix = "#" * max(acc, 0) + "b" * max(-acc, 0)
        out.append("%d%s:%s" % (deg + steps, suffix, b))
    return " ".join(out)


def scale_rhythm(text, factor):
    out = []
    for tok in text.split():
        d, b = tok.split(":")
        out.append("%s:%g" % (d, float(b) * factor))
    return " ".join(out)


# --------------------------------------------------------------------------------------------
# Song: a circular multitrack buffer
# --------------------------------------------------------------------------------------------


class Song:
    def __init__(self, name, bpm, beats, sr=SR):
        self.sr = sr
        self.bpm = bpm
        self.beat = 60.0 / bpm
        self.beats = beats
        self.length = int(round(beats * self.beat * sr))
        self.tracks = {}
        seed = int(hashlib.md5(("MM2/" + name).encode("utf-8")).hexdigest()[:8], 16)
        self.rng = np.random.default_rng(seed)
        self.kit = make_kit(sr, self.rng)

    def track(self, name):
        if name not in self.tracks:
            self.tracks[name] = np.zeros(self.length)
        return self.tracks[name]

    def add(self, track, voice, m, start, beats, gain=1.0, human=0.04, **kw):
        fn = VOICES[voice] if isinstance(voice, str) else voice
        g = gain * (1.0 + human * self.rng.normal()) if human else gain
        y = fn(hz(m), beats * self.beat, self.sr, self.rng, **kw)
        place_wrap(self.track(track), g * y, start * self.beat * self.sr)

    def hit(self, track, x, start, gain=1.0):
        place_wrap(self.track(track), gain * x, start * self.beat * self.sr)

    def line(self, track, voice, key, text, start, octave=0, gain=1.0, legato=0.92, expect=None, **kw):
        pos = start
        for tok in text.split():
            d, b = tok.split(":")
            b = float(b)
            if d != "r":
                deg, acc = parse_deg(d)
                self.add(track, voice, key.deg(deg, acc, octave), pos, b * legato, gain, **kw)
            pos += b
        if expect is not None and abs(pos - start - expect) > 1e-6:
            raise AssertionError("%s: line lasts %g beats, expected %g" % (track, pos - start, expect))
        return pos

    # Accompaniment patterns ----------------------------------------------------------------

    def pad(self, track, voice, chords, start, bpc, center, count=3, gain=1.0, legato=1.0, **kw):
        for i, c in enumerate(chords):
            for m in c.close(center, count):
                self.add(track, voice, m, start + i * bpc, bpc * legato, gain, **kw)

    def arp(self, track, voice, chords, start, bpc, step, pattern, center, gain=1.0, legato=1.0,
            accent=None, **kw):
        for i, c in enumerate(chords):
            tones = c.notes(center, center + 30)
            k = 0
            pos = 0.0
            while pos < bpc - 1e-9:
                idx = pattern[k % len(pattern)]
                if idx is not None:
                    g = gain * (accent[k % len(accent)] if accent else 1.0)
                    self.add(track, voice, tones[min(idx, len(tones) - 1)], start + i * bpc + pos, step * legato,
                             g, **kw)
                pos += step
                k += 1

    def bass(self, track, voice, chords, start, bpc, pattern, gain=1.0, **kw):
        """pattern: list of (beat offset, beats, which) with which in r/o/f/t/b/5- (fifth below)."""
        for i, c in enumerate(chords):
            for off, beats, which in pattern:
                m = {"r": c.root, "o": c.root + 12, "f": c.fifth(), "t": c.third(), "b": c.bass,
                     "5-": c.fifth() - 12}[which]
                self.add(track, voice, m, start + i * bpc + off, beats, gain, **kw)

    def strum(self, track, chords, start, bpc, rhythm, center, gain=1.0, count=4, t60=1.0, mute=0.25):
        """Guitar strums: rhythm = list of (beat offset, 'D'/'U', gain)."""
        for i, c in enumerate(chords):
            tones = c.close(center, count)
            for off, direction, g in rhythm:
                order = tones if direction == "D" else tones[::-1]
                for j, m in enumerate(order):
                    self.add(track, "pluck", m, start + i * bpc + off + j * 0.012 / self.beat, mute,
                             gain * g * (1 - 0.08 * j), t60=t60, bright=0.55)

    def drums(self, track, start, bars, meter, pattern, gain=1.0):
        """pattern: dict kit-name -> list of (beat in bar, gain)."""
        for bar in range(bars):
            for name, hits in pattern.items():
                for b, g in hits:
                    self.hit(track, self.kit[name], start + bar * meter + b,
                             gain * g * (1.0 + 0.05 * self.rng.normal()))

    def roll(self, track, sound, start, beats, rate=0.125, g0=0.2, g1=1.0):
        k = int(round(beats / rate))
        for i in range(k):
            self.hit(track, sound, start + i * rate, g0 + (g1 - g0) * i / max(1, k - 1))


def master(song, spec, reverb=(1.8, 5000.0), echo=None, lp=10000.0, chorus_tracks=(), rms_db=-18.5,
           ceil_db=-6.0, hp=35.0):
    """spec: track -> (gain, reverb send, echo send)."""
    sr = song.sr
    n = song.length
    dry = np.zeros(n)
    rev_send = np.zeros(n)
    echo_send = np.zeros(n)
    for name, y in song.tracks.items():
        g, rs, es = spec.get(name, (1.0, 0.2, 0.0))
        if name in chorus_tracks:
            y = 0.6 * y + 0.6 * chorus_circular(y, sr, loop_seconds=n / sr)
        dry += g * y
        rev_send += g * rs * y
        echo_send += g * es * y
    if echo is not None:
        delay_beats, fb, taps = echo
        e = conv_circular(echo_send, echo_ir(sr, delay_beats * song.beat, fb, taps))
        dry += e
        rev_send += 0.3 * e
    ir = reverb_ir(sr, reverb[0], np.random.default_rng(7), damp=reverb[1])
    mix = dry + conv_circular(rev_send, ir) * 0.9
    if os.environ.get("MM_DEBUG"):
        total = np.sqrt(np.mean(mix ** 2))
        for name, y in sorted(song.tracks.items()):
            g = spec.get(name, (1.0,))[0]
            print("    %-8s %6.1f dB" % (name, 20 * np.log10(np.sqrt(np.mean((g * y) ** 2)) / total + 1e-12)))
    mix = lp_c(mix, lp)
    mix = hp_c(mix, hp, order=1)
    return finish(mix, sr, rms_db, ceil_db)


def finish(x, sr, rms_db, ceil_db):
    """Loudness-match, then a smooth circular look-ahead limiter, then peak to ceil_db."""
    x = x - np.mean(x)
    x = x * (10 ** (rms_db / 20.0) / np.sqrt(np.mean(x ** 2)))
    ceil = 10 ** (ceil_db / 20.0)
    w = int(0.006 * sr)
    env = maximum_filter1d(np.abs(x), size=2 * w + 1, mode="wrap")
    gain = np.minimum(1.0, ceil / np.maximum(env, 1e-9))
    gain = minimum_filter1d(gain, size=2 * w + 1, mode="wrap")
    gain = uniform_filter1d(gain, size=2 * w + 1, mode="wrap")
    x = x * gain
    if os.environ.get("MM_DEBUG"):
        print("    limiter: min gain %.1f dB, %.2f%% of samples below -1 dB, peak before %.1f dB" %
              (20 * np.log10(np.min(gain)), 100 * np.mean(gain < 10 ** (-1 / 20.0)),
               20 * np.log10(np.max(np.abs(x / gain)))))
    return x * (ceil / np.max(np.abs(x)))


# --------------------------------------------------------------------------------------------
# 1. Intro Theme: cinematic, D major / D minor / F major / D major, 75 BPM, 20 bars (64 s)
# --------------------------------------------------------------------------------------------


def intro_theme():
    s = Song("Intro Theme", 75, 80)
    kit = s.kit
    D = Key("D4", MAJOR)
    Dm = Key("D4", MINOR)
    F = Key("F4", MAJOR)

    # Panel 1 (bars 1-4): the village under the glowing Tree. Celesta motif over strings and harp.
    ch = D.chords("0 5 3 4")
    s.pad("pad", "strings", ch, 0, 4, 62, 3, gain=0.14, a=0.8, r=1.0)
    s.arp("harp", "pluck", ch, 0, 4, 0.5, [0, 1, 2, 3, 4, 3, 2, 1], 50, gain=0.26, t60=1.8, bright=0.45)
    s.bass("bass", "sine", ch, 0, 4, [(0, 4, "r")], gain=0.35, a=0.3, r=0.6)
    s.line("lead", "bell", D, MOTIF + "  3:1 4:.5 5:.5 4:2  2:1.5 1:.5 1:2", 0, octave=1, gain=0.42,
           expect=16, kind="celesta", decay=1.4)

    # Panel 2 (bars 5-8): Duskmantle across the moon. D minor, low tremolo, choir, timpani, brass motif.
    ch = Dm.chords("0 5 3 4M7")
    s.pad("pad", "strings", ch, 16, 4, 50, 3, gain=0.26, a=0.4, r=0.6, tremolo=0.6, lp=1600)
    s.pad("choir", "choir", ch, 16, 4, 57, 3, gain=0.16, vowel="oo", a=1.0, r=1.0)
    s.bass("bass", "sine", ch, 16, 4, [(0, 4, "r")], gain=0.6, a=0.2, r=0.6)
    s.line("brass", "brass", Dm, "-3:.5 0:.5 1:.5 2:2.5  r:4  -3:.5 0:.5 1:.5 2:1.5 1b:1  0:1 -1#:3", 16,
           octave=-1, gain=0.45, expect=16, dark=500, bright=2200)
    for b in (16, 24):
        s.roll("perc", timpani(midi("D2"), SR, s.rng, 0.5), b, 3.5, rate=0.125, g0=0.05, g1=0.5)
        s.hit("perc", timpani(midi("D2"), SR, s.rng, 1.2), b + 4, 0.9)
    for b in range(16, 32, 2):   # the moth's wing-beats: soft low toms
        s.hit("perc", tom(midi("A1"), SR, s.rng, 0.3), b + 1, 0.25)

    # Panel 3 (bars 9-12): the Glowspores torn away. Brass motif in full, 16th strings, timpani.
    ch = Dm.chords("0 5 6 4M")
    s.arp("ost", "strings", ch, 32, 4, 0.25, [0, 1, 2, 1], 50, gain=0.18, a=0.01, r=0.05, vib=0, lp=2200)
    s.pad("choir", "choir", ch, 32, 4, 57, 3, gain=0.2, vowel="ah", a=0.4, r=0.6)
    s.line("brass", "brass", Dm, MOTIF + "  4:1 3:1 2:1 1:1  -1#:2 r:2", 32, octave=0, gain=0.75, expect=16)
    s.line("brass2", "brass", Dm, MOTIF + "  4:1 3:1 2:1 1:1  -1#:2 r:2", 32, octave=-1, gain=0.5, expect=16)
    s.bass("bass", "sawbass", ch, 32, 4, [(0, 1.5, "r"), (1.5, .5, "r"), (2, 2, "r")], gain=0.5, cutoff=500)
    s.hit("perc", kit["swell"], 32 - 2.2, 0.8)
    s.hit("perc", kit["crash"], 32, 0.8)
    for b in range(32, 48):
        if b % 4 in (0, 2):
            s.hit("perc", timpani(midi("D2") if b % 8 < 4 else midi("A1"), SR, s.rng, 0.6), b, 0.7)
    s.roll("perc", timpani(midi("A1"), SR, s.rng, 0.4), 44, 2, rate=0.0625, g0=0.2, g1=0.9)

    # Panel 4 (bars 13-16): Elder Morel hands Pip the lantern. Tender F major flute over harp.
    ch = F.chords("3 0 1 2M")
    s.pad("pad", "strings", ch, 48, 4, 57, 3, gain=0.2, a=0.6, r=0.8)
    s.arp("harp", "pluck", ch, 48, 4, 0.5, [0, 2, 4, 2, 1, 3, 2, 4], 48, gain=0.32, t60=1.6, bright=0.5)
    s.bass("bass", "pluck", ch, 48, 4, [(0, 2, "r"), (2, 2, "f")], gain=0.5, t60=1.2, bright=0.3)
    s.line("lead", "flute", F, "3:1.5 2:.5 1:1 -2:1  0:2 2:1 4:1  5:1.5 4:.5 3:1 1:1  6:1 4#:1 2:2", 48,
           octave=1, gain=0.42, expect=16)

    # Panel 5 (bars 17-20): dawn, the path ahead. Strings + horns sing the motif; swell back to bar 1.
    ch = D.chords("0 3 1 4")
    s.pad("pad", "strings", ch, 64, 4, 57, 4, gain=0.26, a=0.3, r=0.6)
    s.line("lead", "strings", D, MOTIF + "  2:1 3:1 4:1 5:1  4:4", 64, octave=1, gain=0.45, expect=16, a=0.08)
    s.line("horn", "brass", D, MOTIF + "  2:1 3:1 4:1 5:1  4:4", 64, octave=0, gain=0.32, expect=16,
           bright=1800, dark=500)
    s.arp("harp", "pluck", ch, 64, 4, 0.25, [0, 1, 2, 3], 50, gain=0.22, t60=1.2)
    s.bass("bass", "sine", ch, 64, 4, [(0, 2, "r"), (2, 2, "f")], gain=0.6, a=0.05)
    s.roll("perc", timpani(midi("A1"), SR, s.rng, 0.5), 72, 6, rate=0.125, g0=0.1, g1=0.6)
    s.hit("perc", kit["crash"], 64, 0.5)

    return master(s, {"lead": (1.0, 0.45, 0.25), "pad": (1.0, 0.5, 0), "harp": (1.8, 0.4, 0.2),
                      "bass": (0.55, 0.2, 0), "choir": (1.0, 0.6, 0), "brass": (1.0, 0.35, 0.1),
                      "brass2": (1.0, 0.3, 0), "horn": (1.6, 0.4, 0), "ost": (1.4, 0.3, 0),
                      "perc": (0.9, 0.35, 0)},
                  reverb=(2.6, 4500.0), echo=(0.75, 0.3, 4), rms_db=-19.0)


# --------------------------------------------------------------------------------------------
# 2. Menu Theme: G major, 96 BPM, 24 bars (60 s). Music box + harp + strings, flute in B.
# --------------------------------------------------------------------------------------------


def menu_theme():
    s = Song("Menu Theme", 96, 96)
    G = Key("G4", MAJOR)
    a_mel = MOTIF + "  3:.5 4:.5 5:1 4:1 2:1  1:2 r:1 -3:1  " + \
        "-3:.5 0:.5 1:.5 2:1.5 4:1  5:.5 4:.5 2:3  1:1 2:.5 3:.5 5:1 3:1  1:3 r:1"
    a_ch = G.chords("0 5 3 4 0 5 1m7 4")
    b_mel = "4:1.5 3:.5 2:1 0:1  1:2 -1:1 1:1  2:1.5 1:.5 0:1 2:1  4:3 r:1  " + \
        "5:1.5 4:.5 3:1 2:1  1:1 2:.5 1:.5 -1:2  0:1 2:1 4:1 7:1  6:2 5:1 4:1"
    b_ch = G.chords("3 4 2 5 1 4 0 4M7")

    for sec, start in (("A", 0), ("B", 32), ("A2", 64)):
        if sec == "B":
            s.line("lead", "flute", G, b_mel, start, octave=1, gain=0.4, expect=32)
            s.line("lead2", "bell", G, b_mel, start, octave=1, gain=0.12, expect=32, kind="musicbox", decay=0.8)
            ch = b_ch
        else:
            s.line("lead", "bell", G, a_mel, start, octave=1, gain=0.45, expect=32, kind="musicbox", decay=1.1)
            ch = a_ch
            if sec == "A2":
                # Flute countermelody: sustained thirds under the music box.
                s.line("counter", "flute", G, "2:4 0:4 0:4 -1:4 2:4 2:4 3:4 1:4", start, octave=0, gain=0.25,
                       expect=32, vib=0.15)
        s.pad("pad", "strings", ch, start, 4, 55, 3, gain=0.17, a=0.5, r=0.6)
        s.arp("harp", "pluck", ch, start, 4, 0.5, [0, 1, 2, 3, 4, 3, 2, 1], 43, gain=0.28, t60=1.4,
              accent=[1.0, 0.7, 0.8, 0.7])
        s.bass("bass", "pluck", ch, start, 4, [(0, 1.5, "b"), (2, 1.5, "f")], gain=0.55, t60=0.9, bright=0.3)
        pat = {"shaker": [(1, 0.35), (3, 0.35), (3.5, 0.2)]}
        if sec == "A2":
            pat["kick"] = [(0, 0.35), (2.5, 0.2)]
            pat["rim"] = [(2, 0.3)]
        s.drums("perc", start, 8, 4, pat)
    s.hit("perc", s.kit["swell"], 64 - 2.4, 0.25)

    return master(s, {"lead": (1.0, 0.4, 0.3), "lead2": (1.0, 0.4, 0.3), "counter": (1.0, 0.4, 0),
                      "pad": (1.0, 0.5, 0), "harp": (1.8, 0.35, 0.2), "bass": (2.4, 0.15, 0),
                      "perc": (1.6, 0.2, 0)},
                  reverb=(2.0, 5000.0), echo=(0.75, 0.3, 4), rms_db=-19.0)


# --------------------------------------------------------------------------------------------
# 3. Overworld Theme: F major march, 120 BPM, 32 bars (64 s). Piccolo, clarinet, tuba, snare.
# --------------------------------------------------------------------------------------------


def overworld_theme():
    s = Song("Overworld Theme", 120, 128)
    F = Key("F4", MAJOR)
    a1 = MOTIF.split("  ")[0] + "  2:.5 1:.5 0:1 r:.5 -3:.25 -2:.25 0:1  1:.75 0:.25 1:.5 2:.5 4:1 1:1  " + \
        "6:.75 5:.25 4:1 r:2  3:.75 3:.25 5:.5 3:.5 2:1 1:1  0:.75 2:.25 4:1 7:1 r:1  " + \
        "6:.5 5:.5 4:.5 3:.5 2:.5 1:.5 6:1  7:1 4:1 0:1 r:1"
    a2 = a1.rsplit("  ", 1)[0] + "  1:1 -1:1 -3:1 r:1"
    a_ch = F.chords("0 0 4 4 3 0 47 0")
    a2_ch = F.chords("0 0 4 4 3 0 47 47")
    b = "5:1 4:.5 5:.5 7:1 5:1  6:1.5 5:.5 4:2  3:1 5:.5 7:.5 8:1 7:1  7:3 r:1  " + \
        "1:1 3:.5 5:.5 8:1 7:1  5:1.5 4:.5 2:2  3:1 2:1 1:1 3:1  4:1 1:1 -1:1 -3:1"
    b_ch = F.chords("5 2 3 0 1 5 3 47")
    plan = (("A", 0, a1, a_ch), ("A2", 32, a2, a2_ch), ("B", 64, b, b_ch), ("A3", 96, a1, a_ch))
    for sec, start, mel, ch in plan:
        if sec == "B":
            s.line("clar", "pulse", F, mel, start, octave=0, gain=0.3, expect=32, duty=0.5, lp=2200, vib=0.15,
                   s=0.8, legato=0.9)
            s.line("lead", "flute", F, mel, start, octave=1, gain=0.18, expect=32, legato=0.85)
        else:
            s.line("lead", "flute", F, mel, start, octave=1, gain=0.42, expect=32, legato=0.8, a=0.02, vib=0.12)
            s.line("lead2", "pulse", F, mel, start, octave=0, gain=0.14, expect=32, duty=0.25, legato=0.75,
                   lp=4000)
            if sec == "A3":
                s.line("glock", "bell", F, mel, start, octave=1, gain=0.14, expect=32, kind="glock", decay=0.6)
            if sec in ("A2", "A3"):
                # Clarinet countermelody in held chord thirds.
                cm = "-1:4 1:2 -1:2 0:2 3:2 2:4 1:2 3:2 2:4"
                s.line("clar", "pulse", F, cm, start + 8, octave=0, gain=0.2, expect=24, duty=0.5, lp=1800,
                       vib=0.12, s=0.85)
        # Tuba oom (beats 1, 3) and brass pah (beats 2, 4).
        s.bass("bass", "brass", ch, start, 4, [(0, 0.9, "r"), (2, 0.9, "5-")], gain=0.55, bright=900,
               dark=300, a=0.015, vib=0)
        for i, c in enumerate(ch):
            for off in (1, 3):
                for m in c.close(53, 3):
                    s.add("pah", "brass", m, start + i * 4 + off, 0.45, 0.13, bright=2500, dark=700, a=0.01,
                          r=0.06, vib=0)
        pat = {"kick": [(0, 0.8), (2, 0.6)],
               "snare": [(1, 0.35), (1.75, 0.25), (3, 0.4), (3.5, 0.2), (3.75, 0.25)],
               "hat": [(0.5, 0.25), (1.5, 0.25), (2.5, 0.25)]}
        if sec == "B":
            pat = {"kick": [(0, 0.6)], "snare": [(1, 0.25), (3, 0.3)], "tamb": [(1, 0.35), (3, 0.35)],
                   "hat": [(0.5, 0.2), (1.5, 0.2), (2.5, 0.2), (3.5, 0.2)]}
        s.drums("drums", start, 8, 4, pat)
        s.hit("drums", s.kit["crash"], start, 0.45)
        s.roll("drums", s.kit["snare"], start + 30, 2, rate=0.125, g0=0.15, g1=0.55)

    return master(s, {"lead": (1.0, 0.25, 0.15), "lead2": (1.0, 0.2, 0.1), "clar": (1.0, 0.3, 0),
                      "glock": (1.0, 0.3, 0.2), "bass": (1.0, 0.1, 0), "pah": (1.0, 0.2, 0),
                      "drums": (1.1, 0.15, 0)},
                  reverb=(1.4, 6000.0), echo=(0.5, 0.2, 3), rms_db=-18.5)


# --------------------------------------------------------------------------------------------
# 4. Village Theme: Bb major waltz (3/4), 138 BPM, 48 bars (62.6 s). Accordion, guitar, fiddle.
# --------------------------------------------------------------------------------------------


def village_theme():
    s = Song("Village Theme", 138, 144)
    Bb = Key("Bb4", MAJOR)
    motif3 = "-3:1 0:1 1:1  2:2 4:1  2:1 1:1 0:1  0:3"
    a = motif3 + "  1:1 2:1 3:1  4:2 3:1  3:1 2:1 1:1  1:3  " + \
        "-3:1 0:1 1:1  2:2 4:1  5:1 4:1 2:1  4:3  5:1 4:1 3:1  2:1 1:1 0:1  1:1.5 2:.5 1:1  0:3"
    a_ch = Bb.chords("0 0 5 3 4 47 47 4 0 0 3 0 3 0 47 0")
    b = "5:1 4:1 5:1  7:2 5:1  8:1 7:1 6:1  4#:3  5:1 6:1 7:1  8:1.5 7:.5 5:1  3:1 5:1 8:1  7:2 6:1  " + \
        "5:1 4:1 5:1  7:1 8:1 9:1  10:2 8:1  9:1 8:1 6:1  7:2 4:1  5:1 6:1 7:1  6:1 7:1 8:1  8:1 6:1 4:1"
    b_ch = Bb.chords("5 5 1 5 5 3 1 5 5 3 1 47 0 5 1 47")
    # Bars 20 and 24 are D major / D7 (V of G minor), which lie outside Bb major: set them explicitly.
    dmaj = Key("D4", MAJOR).chord("0", -2)
    b_ch[3] = dmaj
    b_ch[7] = Chord(dmaj.root, [0, 4, 7, 10], dmaj.root)

    for sec, start, mel, ch in (("A", 0, a, a_ch), ("B", 48, b, b_ch), ("A2", 96, a, a_ch)):
        if sec == "B":
            s.line("fiddle", "strings", Bb, mel, start, octave=0, gain=0.42, expect=48, a=0.05, vib=0.2,
                   lp=3500, legato=0.95)
            s.line("acc", "accordion", Bb, shift_motif(mel, -2), start, octave=0, gain=0.16, expect=48)
        else:
            s.line("acc", "accordion", Bb, mel, start, octave=0, gain=0.4, expect=48, legato=0.9)
            if sec == "A2":
                s.line("fiddle", "strings", Bb, shift_motif(mel, -2), start, octave=0, gain=0.2, expect=48,
                       a=0.06, vib=0.18, lp=3000)
                s.line("glock", "bell", Bb, mel, start, octave=1, gain=0.1, expect=48, kind="celesta", decay=0.7)
        # Oom-pah-pah: plucked bass on 1 (root / fifth alternate), guitar chord on 2 and 3.
        for i, c in enumerate(ch):
            bar = start + i * 3
            m = c.bass if i % 2 == 0 else c.fifth() - 12
            s.add("bass", "pluck", m, bar, 0.9, 0.7, t60=1.0, bright=0.35)
            for off in (1, 2):
                for j, mm in enumerate(c.close(55, 3)):
                    s.add("gtr", "pluck", mm, bar + off + j * 0.01, 0.4, 0.17, t60=0.6, bright=0.55, ring=False,
                          r=0.08)
        pat = {"tamb": [(1, 0.3), (2, 0.3)], "shaker": [(0, 0.25), (1.5, 0.15), (2.5, 0.15)]}
        if sec == "B":
            pat["wood"] = [(0, 0.25)]
        s.drums("perc", start, 16, 3, pat)

    return master(s, {"acc": (0.7, 0.3, 0), "fiddle": (1.0, 0.35, 0), "glock": (1.3, 0.3, 0),
                      "bass": (1.6, 0.1, 0), "gtr": (1.9, 0.2, 0), "perc": (1.1, 0.15, 0)},
                  reverb=(1.3, 5500.0), rms_db=-19.0)


# --------------------------------------------------------------------------------------------
# 5. Cavern Theme: A minor, 84 BPM, 24 bars (68.6 s). Echoing marimba ostinato, flute, celesta.
# --------------------------------------------------------------------------------------------


def cavern_theme():
    s = Song("Cavern Theme", 84, 96)
    Am = Key("A4", MINOR)
    ch_a = Am.chords("0 0 5 5 3 3 4M 4M")
    ch_b = Am.chords("0 0 5 5 3 3 4M 4M")
    ch_c = Am.chords("0 2 5 3 0 2 3 4M")
    allch = ch_a + ch_b + ch_c
    # Marimba ostinato, rippling through a dotted-eighth echo.
    s.arp("ost", "bell", allch, 0, 4, 0.5, [0, 2, 3, 4, 5, 4, 3, 2], 45, gain=0.3, kind="marimba", decay=1.0,
          accent=[1.0, 0.6, 0.8, 0.6, 0.9, 0.6, 0.8, 0.6])
    # Drone + bass.
    for bar in range(24):
        s.add("drone", "sine", midi("A1"), bar * 4, 4, 0.35, a=0.5, r=0.5, d=1, s=1)
    s.bass("bass", "tri", allch, 0, 4, [(0, 3.5, "r")], gain=0.35, a=0.05, r=0.3)
    # B: flute melody (echoing far down the tunnels).
    b = "4:2 2:1 3:1  4:3 r:1  5:1.5 4:.5 2:1 0:1  2:3 r:1  3:1 5:1 7:1 5:1  4:2 3:1 2:1  " + \
        "1:1 -1#:1 1:1 4:1  4:3 r:1"
    s.line("lead", "flute", Am, b, 32, octave=0, gain=0.45, expect=32, vib=0.25, breath=0.05)
    # C: the Glowspore motif, slowed and minor, on celesta; flute sustains under it.
    c = scale_rhythm("-3:.5 0:.5 1:.5 2:1.5 4:1", 2) + "  2:1 1:1 0:2  -2:4  " + \
        scale_rhythm("-3:.5 0:.5 1:.5 2:1.5 4:1", 2) + "  3:1 2:1 1:2  -1#:4"
    s.line("bells", "bell", Am, c, 64, octave=1, gain=0.4, expect=32, kind="celesta", decay=1.6)
    s.line("lead", "flute", Am, "2:8 0:4 -2:4 2:8 0:4 -1#:4", 64, octave=0, gain=0.2, expect=32, vib=0.15)
    s.pad("pad", "strings", ch_c, 64, 4, 52, 3, gain=0.12, a=1.0, r=1.0, lp=1400)
    # A: sparse "crystal" pings on high pentatonic notes.
    pent = [midi(n) for n in ("A6", "C7", "D7", "E7", "G6", "E6")]
    for k in range(18):
        beat = (k * 5.25 + (k % 3) * 0.5) % 96
        s.add("ping", "bell", pent[k % len(pent)], beat, 0.5, 0.12 + 0.05 * (k % 2), kind="glass", decay=0.35)
    # Heartbeat toms.
    for bar in range(24):
        s.hit("perc", tom(midi("E2"), SR, s.rng, 0.25), bar * 4, 0.35)
        s.hit("perc", tom(midi("A1"), SR, s.rng, 0.3), bar * 4 + 2.5, 0.25)
        if bar >= 8:
            s.hit("perc", s.kit["shaker"], bar * 4 + 1, 0.12)
            s.hit("perc", s.kit["shaker"], bar * 4 + 3, 0.12)

    return master(s, {"ost": (1.0, 0.5, 0.6), "drone": (0.45, 0.2, 0), "bass": (0.8, 0.2, 0),
                      "lead": (1.0, 0.55, 0.5), "bells": (1.0, 0.6, 0.5), "pad": (1.0, 0.5, 0),
                      "ping": (1.0, 0.8, 0.6), "perc": (0.9, 0.5, 0.2)},
                  reverb=(3.4, 3500.0), echo=(0.75, 0.45, 6), lp=8000, rms_db=-19.5)


# --------------------------------------------------------------------------------------------
# 6. Grotto Theme: Eb lydian, 88 BPM, 24 bars (65.5 s). Vibes triplets, chorus pad, wobbly sine lead.
# --------------------------------------------------------------------------------------------


def grotto_theme():
    s = Song("Grotto Theme", 88, 96)
    Eb = Key("Eb4", MAJOR)
    ch_a = Eb.chords("0 1M 0 1M 5 3 4s 4")
    ch_b = Eb.chords("0 1M 0 1M 5 3 4 4")
    ch_c = Eb.chords("0 3 1 4 0 1M 3 4")
    allch = ch_a + ch_b + ch_c
    s.arp("vibes", "bell", allch, 0, 4, 1.0 / 3, [0, 1, 2, 3, 2, 1], 51, gain=0.22, kind="vibes", decay=0.9,
          tremolo=0.35, accent=[1.0, 0.6, 0.7])
    s.pad("pad", "strings", allch, 0, 4, 55, 4, gain=0.12, a=1.2, r=1.2, lp=1500, vib=0.25)
    s.bass("bass", "sine", allch, 0, 4, [(0, 2.5, "r"), (2.67, 1.2, "f")], gain=0.55, a=0.08, r=0.3, h2=0.2)
    b = "4:2 2:1 4:1  5:1.5 3#:.5 2:2  6:1 5:1 4:1 2:1  3#:2 1:2  2:1 4:1 7:2  7:1 6:.5 5:.5 3:2  " + \
        "4:1 5:1 6:1 8:1  8:2 4:2"
    s.line("lead", "sine", Eb, b, 32, octave=1, gain=0.42, expect=32, vib=0.35, a=0.06, r=0.25, h2=0.15)
    c = MOTIF + "  3:1 2:.5 1:.5 3:2  1:4  " + MOTIF.split("  ")[0] + "  5:.5 3#:.5 2:3  " + \
        "5:1 4:1 3:1 1:1  1:2 -3:2"
    s.line("lead", "sine", Eb, c, 64, octave=1, gain=0.42, expect=32, vib=0.35, a=0.05, r=0.3, h2=0.15)
    s.line("lead2", "bell", Eb, c, 64, octave=1, gain=0.12, expect=32, kind="celesta", decay=1.0)
    # Harp glissandi at section turns.
    for g0 in (30.5, 62.5, 94.5):
        for k in range(12):
            s.add("harp", "pluck", Eb.deg(k, 0, -1), g0 + k * 0.125, 0.5, 0.18, t60=1.5)
    # Bubbles as musical sparkles: quick rising sine blips.
    for k in range(20):
        beat = (k * 4.75 + (k * 7 % 5) * 0.33) % 96
        n = int(0.06 * SR)
        t = np.arange(n) / SR
        f0 = 700 + 90 * (k * 13 % 9)
        blip = np.sin(2 * np.pi * np.cumsum(f0 * (1 + 3 * t / 0.06)) / SR) * np.exp(-t / 0.02)
        s.hit("bub", blip, beat, 0.12)
    s.drums("perc", 0, 24, 4, {"brush": [(1, 0.3), (3, 0.3)], "shaker": [(0, 0.1), (2.67, 0.1)]})

    return master(s, {"vibes": (1.0, 0.5, 0.3), "pad": (1.3, 0.6, 0), "bass": (0.5, 0.15, 0),
                      "lead": (1.0, 0.5, 0.35), "lead2": (1.2, 0.5, 0.3), "harp": (3.0, 0.5, 0.2),
                      "bub": (5.0, 0.6, 0.3), "perc": (3.0, 0.3, 0)},
                  chorus_tracks=("pad", "lead", "vibes"), reverb=(2.8, 3000.0), echo=(1.0, 0.35, 4),
                  lp=6500, rms_db=-19.5)


# --------------------------------------------------------------------------------------------
# 7. Canopy Theme: A major / mixolydian, 116 BPM, 32 bars (66.2 s). Pan flute, guitar, marimba.
# --------------------------------------------------------------------------------------------


def canopy_theme():
    s = Song("Canopy Theme", 116, 128)
    A = Key("A4", MAJOR)
    a1 = MOTIF.split("  ")[0] + "  3:1 2:.5 1:.5 1:2  3:.5 4:.5 5:1 4:1 3:1  2:3 r:1  " + \
        "5:.5 4:.5 5:1 7:1 5:1  4:1 3:.5 2:.5 3:2  1:1 2:1 4:1 6:1  6:2 4:2"
    a2 = a1.rsplit("  ", 2)[0] + "  1:1 2:1 4:1 6:1  7:4"
    ch_a = A.chords("0 b6M 3 0 5 3 4 4")
    ch_a2 = A.chords("0 b6M 3 0 5 3 4 0")
    b = "5:3 4:1  4:2 1:2  2:3 4:1  5:4  7:2 5:1 4:1  6:3 4:1  3:2 5:2  4:1 6:1 8:2"
    ch_b = A.chords("3 4 2 5 3 4 1 4")
    c = MOTIF + "  3:1 2:.5 1:.5 1:2  -3:.5 -2:.5 0:3  0:.5 2:.5 3:.5 4:1.5 5:1  4:.5 3:.5 2:3  " + \
        "1:1 2:1 4:1 6:1  4:4"
    ch_c = A.chords("0 3 b6M 0 5 3 4 4")
    plan = (("A", 0, a1, ch_a), ("A2", 32, a2, ch_a2), ("B", 64, b, ch_b), ("C", 96, c, ch_c))
    strum = [(0, "D", 1.0), (1, "D", 0.6), (1.5, "U", 0.5), (2.5, "U", 0.55), (3, "D", 0.7), (3.5, "U", 0.5)]
    for sec, start, mel, ch in plan:
        if sec == "B":
            s.line("lead", "strings", A, mel, start, octave=1, gain=0.42, expect=32, a=0.3, vib=0.18)
            s.line("lead2", "flute", A, mel, start, octave=0, gain=0.22, expect=32, breath=0.15)
            s.pad("pad", "strings", ch, start, 4, 57, 3, gain=0.16, a=0.6)
            s.arp("mar", "bell", ch, start, 4, 1.0, [0, 2, 1, 3], 57, gain=0.2, kind="marimba")
        else:
            s.line("lead", "flute", A, mel, start, octave=1, gain=0.45, expect=32, breath=0.14, vib=0.18, a=0.04)
            s.arp("mar", "bell", ch, start, 4, 0.5, [0, 2, 1, 3, 2, 4, 3, 1], 57, gain=0.2, kind="marimba",
                  accent=[1, 0.6, 0.8, 0.6])
            if sec == "C":
                s.line("whistle", "sine", A, shift_motif(mel, 2), start, octave=1, gain=0.16, expect=32, vib=0.3,
                       a=0.03)
        s.strum("gtr", ch, start, 4, strum, 52, gain=0.16, t60=0.9, mute=0.3)
        s.bass("bass", "pluck", ch, start, 4, [(0, 1.5, "r"), (1.5, 0.5, "r"), (2, 1.5, "f"), (3.5, .5, "o")],
               gain=0.55, t60=0.8, bright=0.3)
        pat = {"shaker": [(e * 0.5, 0.18 if e % 2 else 0.1) for e in range(8)],
               "kick": [(0, 0.45), (2.5, 0.3)]}
        if sec != "B":
            pat["clap"] = [(1, 0.18), (3, 0.18)]
        s.drums("perc", start, 8, 4, pat)
        for bar in range(8):
            s.hit("perc", tom(midi("D4"), SR, s.rng, 0.08), start + bar * 4 + 1.5, 0.18)
            s.hit("perc", tom(midi("A3"), SR, s.rng, 0.1), start + bar * 4 + 3.75, 0.16)
    s.hit("perc", s.kit["swell"], 64 - 2.2, 0.3)

    return master(s, {"lead": (1.0, 0.35, 0.25), "lead2": (1.0, 0.35, 0.2), "whistle": (1.0, 0.4, 0.3),
                      "pad": (1.0, 0.5, 0), "mar": (1.0, 0.3, 0.2), "gtr": (2.8, 0.25, 0),
                      "bass": (1.8, 0.1, 0), "perc": (1.0, 0.2, 0)},
                  reverb=(1.8, 6000.0), echo=(0.75, 0.25, 3), rms_db=-18.5)


# --------------------------------------------------------------------------------------------
# 8. Hollow Theme: E minor + Phrygian b2, 72 BPM, 20 bars (66.7 s). Choir, detuned celesta, heartbeat.
# --------------------------------------------------------------------------------------------


def hollow_theme():
    s = Song("Hollow Theme", 72, 80)
    Em = Key("E4", MINOR)
    ch = Em.chords("0 0 b1M 0 3 5 b1M 4M " + "0 0 b1M 0 5m 3 b1M 4M " + "0 b1M 0 4M7")
    s.pad("choir", "choir", ch, 0, 4, 52, 3, gain=0.17, vowel="oo", a=1.2, r=1.4, vib=0.1)
    s.bass("drone", "sine", ch, 0, 4, [(0, 4, "r")], gain=0.4, a=0.8, r=0.8, d=2, s=1)
    # Heartbeat: low thumps on 1 and the "and" of 1.
    for bar in range(20):
        s.hit("perc", tom(midi("E1"), SR, s.rng, 0.3), bar * 4, 0.5)
        s.hit("perc", tom(midi("E1"), SR, s.rng, 0.25), bar * 4 + 0.6, 0.3)
    # Reverse swells into each 4-bar phrase.
    for b in (14, 30, 46, 62, 78):
        s.add("swell", "strings", Em.deg(0, 0, -1), b, 1.95, 0.25, a=1.9, r=0.05, d=5, s=1, lp=1800)
        s.add("swell", "strings", Em.deg(1, -1, -1), b, 1.95, 0.18, a=1.9, r=0.05, d=5, s=1, lp=1800)
    # Gong at each big section.
    for b in (0, 32, 64):
        s.hit("perc", gong(midi("E2"), SR, s.rng, 3.0), b, 0.45)
    # Bars 1-8: glassy plinks, distant horn quoting the motif's opening.
    plinks = [(0.5, "B5"), (3, "F5"), (6.5, "G5"), (9, "E6"), (13, "C6"), (17.5, "B5"), (21, "F6"), (26, "D#6"),
              (29.5, "A5")]
    for beat, nm in plinks:
        s.add("glass", "bell", midi(nm), beat, 0.5, 0.18, kind="glass", decay=0.9, detune=14)
    s.line("horn", "brass", Em, "-3:1 0:1 1b:1 2:5", 16, octave=-1, gain=0.3, expect=8, bright=1200, dark=400)
    # Bars 9-16: the motif on a detuned (old, haunted) music box.
    mb = "-3:.5 0:.5 1b:.5 2:1.5 4:1  2:.5 1b:.5 0:3  5:2 3:1 2:1  1b:3 r:1  4:1 2:1 5:2  3:1.5 2:.5 0:2  " + \
        "1b:1 3:1 5:2  4:1 -1#:1 1#:2"
    s.line("mbox", "bell", Em, mb, 32, octave=1, gain=0.42, expect=32, kind="musicbox", decay=1.3, detune=22)
    s.pad("trem", "strings", ch[8:16], 32, 4, 59, 3, gain=0.1, tremolo=0.7, a=0.5, lp=2000)
    # Bars 17-20: the choir sings the motif slowly against the tritone.
    s.line("choir2", "choir", Em, "-3:1 0:1 1b:1 2:1  4:4  2:1 1b:1 0:2  -1#:4", 64, octave=0, gain=0.26,
           expect=16, vowel="ah", a=0.3, r=0.6)
    s.pad("trem", "strings", ch[16:], 64, 4, 59, 3, gain=0.12, tremolo=0.7, a=0.5, lp=2000)

    return master(s, {"choir": (1.0, 0.7, 0), "drone": (0.45, 0.3, 0), "perc": (0.9, 0.4, 0),
                      "swell": (2.2, 0.5, 0), "glass": (1.3, 0.8, 0.5), "horn": (2.4, 0.6, 0.3),
                      "mbox": (1.0, 0.6, 0.4), "trem": (1.6, 0.5, 0), "choir2": (1.6, 0.7, 0)},
                  reverb=(4.0, 3200.0), echo=(1.5, 0.35, 4), lp=7500, rms_db=-20.0)


# --------------------------------------------------------------------------------------------
# 9. Boss Theme (Grumblecrag): C minor, 160 BPM, 40 bars (60 s). Riff bass, brass, heavy drums.
# --------------------------------------------------------------------------------------------


def boss_theme():
    s = Song("Boss Theme", 160, 160)
    Cm = Key("C4", MINOR)
    riff_degs = ["0", "0", "2", "0", "3", "0", "4b", "4"]
    kit = s.kit

    def riff(start, bars, transpose=0, gain=0.55):
        for bar in range(bars):
            for k, d in enumerate(riff_degs):
                deg, acc = parse_deg(d)
                m = Cm.deg(deg, acc, -2) + transpose
                s.add("riff", "sawbass", m, start + bar * 4 + k * 0.5, 0.42, gain * (1.0 if k % 2 == 0 else 0.8),
                      cutoff=700)

    # Intro bars 1-4: the riff alone with toms + timpani.
    riff(0, 4)
    for bar in range(4):
        s.hit("drums", timpani(midi("C2"), SR, s.rng, 0.5), bar * 4, 0.8)
        for k in range(4):
            s.hit("drums", tom(midi("G2") - k * 2, SR, s.rng, 0.15), bar * 4 + 2 + k * 0.5, 0.35 + 0.08 * k)
    s.hit("drums", kit["swell"], 16 - 2.0, 0.7)

    full = {"kick": [(0, 1.0), (1.5, 0.7), (2, 0.9), (3.5, 0.6)], "snare": [(1, 0.8), (3, 0.85)],
            "hat": [(e * 0.5, 0.35 if e % 2 else 0.2) for e in range(8)]}
    a = "4:1.5 3:.5 2:1 4:1  7:2 6:1 4:1  5:1.5 4:.5 2:1 0:1  1:3 r:1  " + \
        "4:1.5 3:.5 2:1 4:1  7:1 8:1 9:1 7:1  8:1.5 7:.5 5:1 4:1  6#:2 4:2"
    a_ch = Cm.chords("0 0 5 6 0 0 5 4M")
    for start in (16, 112):
        riff(start, 8)
        s.line("lead", "brass", Cm, a, start, octave=0, gain=0.5, expect=32, bright=4000)
        s.line("lead2", "pulse", Cm, a, start, octave=1, gain=0.12, expect=32, duty=0.25, lp=5000, legato=0.85)
        for i, c in enumerate(a_ch):
            for off in (0.5, 2.5):
                for m in c.close(55, 3):
                    s.add("stab", "brass", m, start + i * 4 + off, 0.3, 0.12, a=0.005, r=0.05, vib=0)
        s.drums("drums", start, 8, 4, full)
        s.hit("drums", kit["crash"], start, 0.7)
        if start == 112:
            s.line("counter", "strings", Cm, "2:4 0:4 0:4 1:4 2:4 4:4 2:4 6#:4", start, octave=0, gain=0.22,
                   expect=32, a=0.1)
    # B (bars 13-20): the motif as a defiant brass call.
    b = MOTIF + "  3:1 5:1 7:1 5:1  6#:3 r:1  " + shift_motif(MOTIF, 7) + "  8:1 7:1 6:1 5:1  4:4"
    b_ch = Cm.chords("0 5 3 4M 0 5 6 4M")
    riff(48, 8, gain=0.5)
    s.line("lead", "brass", Cm, b, 48, octave=0, gain=0.55, expect=32, bright=4200)
    s.line("lead2", "brass", Cm, b, 48, octave=-1, gain=0.3, expect=32, bright=2500)
    s.pad("pad", "strings", b_ch, 48, 4, 55, 3, gain=0.16, a=0.1)
    s.drums("drums", 48, 8, 4, full)
    s.hit("drums", kit["crash"], 48, 0.7)
    s.hit("drums", kit["crash"], 64, 0.5)
    # C (bars 21-28): breakdown. Half-time, string 16ths, falling-pebble woodblocks.
    c_ch = Cm.chords("0 0 b1M b1M 0 0 b1M 4M")
    s.arp("ost", "strings", c_ch, 80, 4, 0.25, [0, 1, 2, 1, 3, 1, 2, 1], 55, gain=0.2, a=0.005, r=0.04, vib=0,
          lp=2600)
    s.bass("riff", "sawbass", c_ch, 80, 4, [(0, 1.5, "r"), (1.5, 1.5, "r"), (3, 1, "o")], gain=0.5, cutoff=500)
    s.drums("drums", 80, 8, 4, {"kick": [(0, 1.0), (2.5, 0.5)], "snare": [(2, 0.85)],
                                "hat": [(e * 0.5, 0.25) for e in range(8)]})
    for k in range(16):
        s.hit("drums", kit["wood"], 80 + k * 2 + (k % 3) * 0.25 + 1.25, 0.18 + 0.02 * (k % 4))
    for b0 in (88, 104):
        s.hit("drums", timpani(midi("C2"), SR, s.rng, 0.8), b0, 0.9)
    s.line("lead", "brass", Cm, "0:4 -1:4 1b:8 0:4 2:4 1b:4 -1#:4", 80, octave=-1, gain=0.4, expect=32,
           bright=1500, dark=400)
    s.roll("drums", kit["snare"], 108, 4, rate=0.125, g0=0.15, g1=0.8)
    # Tag (bars 37-40): chromatic climb back into the intro riff.
    for i, d in enumerate(("b5M", "5M", "b6M", "6M")):
        c = Cm.chord(d)
        riff_m = c.root
        for k in range(8):
            s.add("riff", "sawbass", riff_m + (12 if k % 2 else 0), 144 + i * 4 + k * 0.5, 0.42, 0.55, cutoff=800)
        for m in c.close(55, 3):
            s.add("stab", "brass", m, 144 + i * 4, 1.5, 0.22, a=0.01)
            s.add("stab", "brass", m, 144 + i * 4 + 2.5, 1.0, 0.18, a=0.01)
    s.drums("drums", 144, 4, 4, full)
    s.hit("drums", kit["crash"], 144, 0.6)
    s.roll("drums", kit["snare"], 158, 2, rate=0.125, g0=0.2, g1=0.8)

    return master(s, {"riff": (1.3, 0.1, 0), "lead": (1.0, 0.25, 0.1), "lead2": (1.0, 0.2, 0),
                      "stab": (1.4, 0.25, 0), "pad": (1.0, 0.3, 0), "ost": (1.2, 0.25, 0),
                      "counter": (1.4, 0.3, 0), "drums": (0.5, 0.12, 0)},
                  reverb=(1.5, 6000.0), echo=(0.75, 0.2, 2), rms_db=-17.5)


# --------------------------------------------------------------------------------------------
# 10. Duskmantle Theme: F minor, 150 BPM, 44 bars (70.4 s). Three phases: ominous, frantic, defiant.
# --------------------------------------------------------------------------------------------


def duskmantle_theme():
    s = Song("Duskmantle Theme", 150, 176)
    Fm = Key("F4", MINOR)
    kit = s.kit

    # Intro (bars 1-4) and outro (bars 41-44): organ + choir, tolling bells, timpani roll.
    for start in (0, 160):
        ch = Fm.chords("0 5 3 4M")
        s.pad("organ", "organ", ch, start, 4, 53, 4, gain=0.22)
        s.pad("choir", "choir", ch, start, 4, 57, 3, gain=0.2, vowel="ah", a=0.5)
        s.bass("bass", "sawbass", ch, start, 4, [(0, 4, "r")], gain=0.45, cutoff=300, s=1.0)
        s.line("bells", "bell", Fm, "-3:2 0:2 1:2 2:10", start, octave=0, gain=0.35, expect=16, kind="glass",
               decay=0.8)
        s.roll("drums", timpani(midi("C2"), SR, s.rng, 0.4), start + 8, 8, rate=0.125, g0=0.05, g1=0.7)
    s.hit("drums", kit["swell"], 16 - 2.0, 0.6)

    # Phase 1 (bars 5-16): ominous half-time; brass melody over 8th strings.
    ch1 = Fm.chords("0 5 3 4M 0 5 b1M 4M 0 6 5 4M")
    s.arp("ost", "strings", ch1, 16, 4, 0.5, [0, 1, 2, 1], 53, gain=0.22, a=0.01, r=0.05, vib=0, lp=2400)
    s.pad("choir", "choir", ch1, 16, 4, 57, 3, gain=0.14, vowel="oo", a=0.4)
    s.bass("bass", "sawbass", ch1, 16, 4, [(0, 1.5, "r"), (1.5, 1, "r"), (3, 1, "o")], gain=0.45, cutoff=500)
    p1 = "0:2 2:2  4:3 5:1  3:2 2:1 1:1  -1#:4  0:2 2:2  4:3 7:1  8b:2 7:1 5:1  6#:4  " + \
        "0:1 2:1 4:1 7:1  6:2 4:2  5:1 4:1 2:1 1b:1  -1#:2 1:2"
    s.line("lead", "brass", Fm, p1, 16, octave=0, gain=0.5, expect=48, bright=3000)
    s.line("lead2", "brass", Fm, p1, 16, octave=-1, gain=0.3, expect=48, bright=1500)
    s.drums("drums", 16, 12, 4, {"kick": [(0, 1.0), (2.5, 0.5)], "snare": [(2, 0.8)],
                                 "hat": [(1, 0.2), (3, 0.2)]})
    for b0 in range(16, 64, 8):
        s.hit("drums", timpani(midi("F2"), SR, s.rng, 0.6), b0, 0.6)

    # Phase 2 (bars 17-28): frantic. Full-time drums, 16th strings, choir carries the melody, orb stabs.
    ch2 = Fm.chords("0 0 5 5 3 3 4M 4M 0 5 3 4M")
    s.hit("drums", kit["crash"], 64, 0.9)
    s.hit("drums", kit["clap"], 64, 0.8)
    s.arp("ost", "strings", ch2, 64, 4, 0.25, [0, 1, 2, 3, 2, 1], 53, gain=0.2, a=0.005, r=0.04, vib=0, lp=2800)
    p2 = "7:1.5 6:.5 4:1 2:1  4:2 0:2  5:1.5 4:.5 2:1 0:1  2:4  3:1.5 4:.5 5:1 6:1  7:2 5:2  " + \
        "6#:1 7:1 8:1 6#:1  4:4  7:1.5 6:.5 4:1 2:1  5:2 7:2  8:1 7:1 6:1 5:1  6#:2 4:2"
    s.line("choir2", "choir", Fm, p2, 64, octave=0, gain=0.42, expect=48, vowel="ah", a=0.06, r=0.2)
    s.line("lead", "strings", Fm, p2, 64, octave=1, gain=0.26, expect=48, a=0.04)
    s.bass("bass", "sawbass", ch2, 64, 4, [(k * 0.5, 0.45, "r" if k % 4 else "o") for k in range(8)], gain=0.45,
           cutoff=700)
    for i, c in enumerate(ch2):
        for off in (1.5, 3.5):
            for m in c.close(60, 3):
                s.add("stab", "brass", m, 64 + i * 4 + off, 0.25, 0.14, a=0.004, r=0.05, vib=0)
    s.drums("drums", 64, 12, 4, {"kick": [(0, 1.0), (1.5, 0.6), (2, 0.9), (3.5, 0.5)], "snare": [(1, 0.8), (3, 0.85)],
                                 "hat": [(e * 0.5, 0.3 if e % 2 else 0.18) for e in range(8)]})

    # Phase 3 (bars 29-40): defiant. Pip's motif breaks through in F MAJOR over the minor storm.
    ch3 = Fm.chords("5 6 0M 0M 5 6 4M 4M 5 6 0 4M")
    s.hit("drums", kit["crash"], 112, 1.0)
    s.hit("drums", kit["crash"], 120, 0.8)
    p3 = "5:2 4:2  6:1 7:1 8:1 9:1  " + "4:.5 7:.5 8:.5 9#:1.5 11:1  9#:.5 8:.5 7:3" + \
        "  5:1 7:1 9:1 12:1  6:2 8:1 9:1  8:2 6#:2  4:4  5:1.5 4:.5 2:1 0:1  1:2 -1:2  0:4  -1#:2 1:2"
    s.line("lead", "brass", Fm, p3, 112, octave=0, gain=0.55, expect=48, bright=4200)
    s.line("lead2", "brass", Fm, p3, 112, octave=-1, gain=0.32, expect=48, bright=2200)
    s.line("choir2", "choir", Fm, p3, 112, octave=0, gain=0.25, expect=48, vowel="ah", a=0.08)
    s.pad("organ", "organ", ch3, 112, 4, 53, 4, gain=0.14)
    s.arp("ost", "strings", ch3, 112, 4, 0.25, [0, 1, 2, 3, 2, 1, 2, 3], 55, gain=0.18, a=0.005, r=0.04, vib=0,
          lp=3000)
    s.bass("bass", "sawbass", ch3, 112, 4, [(k * 0.5, 0.45, "r" if k % 2 == 0 else "o") for k in range(8)],
           gain=0.45, cutoff=800)
    s.drums("drums", 112, 12, 4, {"kick": [(0, 1.0), (1, 0.7), (2, 0.9), (3, 0.7)], "snare": [(1, 0.8), (3, 0.85)],
                                  "hat": [(e * 0.25, 0.22 if e % 2 else 0.12) for e in range(16)]})
    for b0 in range(112, 160, 4):
        s.hit("drums", timpani(midi("F2") if (b0 // 4) % 2 == 0 else midi("C2"), SR, s.rng, 0.5), b0, 0.6)
    s.roll("drums", kit["snare"], 156, 4, rate=0.125, g0=0.2, g1=0.9)

    return master(s, {"organ": (1.0, 0.5, 0), "choir": (1.0, 0.6, 0), "choir2": (1.0, 0.5, 0),
                      "bells": (1.0, 0.7, 0.4), "ost": (1.0, 0.3, 0), "bass": (1.0, 0.1, 0),
                      "lead": (1.6, 0.35, 0.1), "lead2": (1.4, 0.3, 0), "stab": (2.2, 0.25, 0),
                      "drums": (0.55, 0.2, 0)},
                  reverb=(2.4, 4500.0), echo=(0.75, 0.2, 3), rms_db=-17.5)


# --------------------------------------------------------------------------------------------
# 11. Credits Theme: C major, 72 BPM, 24 bars (80 s). Piano, strings, choir + orchestra, music box.
# --------------------------------------------------------------------------------------------


def credits_theme():
    s = Song("Credits Theme", 72, 96)
    C = Key("C4", MAJOR)
    kit = s.kit
    ch1 = C.chords("0 5 3 4")
    ch2 = C.chords("0 4/6 5 2/4 3 0/2 1 4")
    ch3 = C.chords("3 0/2 17 4 3 4/3 27 5")
    ch4 = C.chords("3 3m 0 47")
    allch = ch1 + ch2 + ch3 + ch4
    # Piano accompaniment all the way through: broken chords.
    s.arp("piano", "piano", allch, 0, 4, 0.5, [0, 2, 3, 4, 3, 2, 1, 2], 48, gain=0.24,
          accent=[1.0, 0.7, 0.8, 0.7, 0.9, 0.7, 0.8, 0.7])
    s.bass("pbass", "piano", allch, 0, 4, [(0, 3.8, "b")], gain=0.32)
    # Bars 1-4: piano melody, the motif.
    s.line("pmel", "piano", C, MOTIF + "  5:1.5 4:.5 3:1 0:1  1:3 r:1", 0, octave=1, gain=0.4, expect=16)
    # Bars 5-12: strings sing the credits melody.
    mel2 = "2:1.5 1:.5 0:1 4:1  4:2 3:1 1:1  2:1.5 1:.5 0:1 5:1  4:3 r:1  " + \
        "5:1.5 4:.5 3:1 7:1  7:2 6:1 4:1  5:1 3:1 5:1 8:1  8:2 7:1 6:1"
    s.line("str", "strings", C, mel2, 16, octave=1, gain=0.5, expect=32, a=0.12, vib=0.16)
    s.pad("pad", "strings", ch2, 16, 4, 52, 3, gain=0.13, a=0.6, r=0.8, lp=1800)
    s.bass("cello", "strings", ch2, 16, 4, [(0, 4, "b")], gain=0.22, a=0.2, lp=1200)
    # Bars 13-20: everyone. The motif soars; choir, horns, timpani, cymbal swell.
    mel3 = "4:.5 7:.5 8:.5 9:1.5 11:1  9:.5 8:.5 7:3  10:1 9:.5 8:.5 10:2  8:2 7:1 8:1  " + \
        "12:1.5 11:.5 10:1 7:1  11:1 10:1 9:1 8:1  9:2 11:2  9:1.5 8:.5 7:2"
    s.hit("perc", kit["swell"], 48 - 2.2, 0.55)
    s.hit("perc", kit["crash"], 48, 0.5)
    s.line("str", "strings", C, mel3, 48, octave=0, gain=0.62, expect=32, a=0.1, vib=0.16)
    s.line("str2", "strings", C, mel3, 48, octave=1, gain=0.22, expect=32, a=0.1, vib=0.16)
    s.line("horn", "brass", C, mel3, 48, octave=-1, gain=0.4, expect=32, bright=1800, dark=500)
    s.pad("choir", "choir", ch3, 48, 4, 55, 3, gain=0.3, vowel="ah", a=0.6)
    s.bass("cello", "strings", ch3, 48, 4, [(0, 2, "b"), (2, 2, "r")], gain=0.26, a=0.15, lp=1200)
    for b0 in range(48, 80, 4):
        s.hit("perc", timpani(ch3[(b0 - 48) // 4].root + 12, SR, s.rng, 0.8), b0, 0.4)
    s.hit("perc", kit["crash"], 64, 0.35)
    # Bars 21-24: coda. Music box remembers the motif; iv minor; G7 home.
    s.line("mbox", "bell", C, MOTIF.split("  ")[0] + "  2b:.5 1:.5 0:3  0:2 2:1 4:1  3:1 1:1 -1:1 r:1", 80,
           octave=1, gain=0.4, expect=16, kind="musicbox", decay=1.3)
    s.pad("pad", "strings", ch4, 80, 4, 52, 3, gain=0.14, a=0.8, r=1.0, lp=1600)
    s.bass("cello", "strings", ch4, 80, 4, [(0, 4, "b")], gain=0.2, a=0.3, lp=1000)

    return master(s, {"piano": (1.0, 0.35, 0), "pbass": (0.55, 0.3, 0), "pmel": (1.2, 0.4, 0.1),
                      "str2": (1.0, 0.5, 0),
                      "str": (1.0, 0.45, 0), "pad": (1.0, 0.5, 0), "cello": (1.0, 0.4, 0),
                      "horn": (1.0, 0.45, 0), "choir": (1.0, 0.6, 0), "perc": (0.8, 0.4, 0),
                      "mbox": (1.0, 0.45, 0.25)},
                  reverb=(2.6, 5000.0), echo=(0.75, 0.25, 3), rms_db=-19.5)


# --------------------------------------------------------------------------------------------
# 12. Starlit Theme: E major, 184 BPM, 12 bars (15.65 s). Fast invincibility loop.
# --------------------------------------------------------------------------------------------


def starlit_theme():
    s = Song("Starlit Theme", 184, 48)
    E = Key("E4", MAJOR)
    ch = E.chords("0 0 b5M b6M 0 0 b5M b6M 3 4 0 4")
    mel = "-3:.5 0:.5 1:.5 2:1 4:.5 2:.5 4:.5  7:1.5 6:.5 4:2  5b:.5 7:.5 9b:1 7:2  6b:.5 8:.5 10:1 8:2  " + \
        "-3:.5 0:.5 1:.5 2:1 4:.5 2:.5 4:.5  7:1.5 6:.5 4:2  5b:.5 7:.5 9b:1 7:2  6b:.5 8:.5 10:1 11:2  " + \
        "10:1 9:.5 8:.5 7:2  8:1 9:.5 10:.5 11:2  14:1 11:1 9:1 7:1  8:1 11:1 13:2"
    s.line("lead", "pulse", E, mel, 0, octave=0, gain=0.4, expect=48, duty=0.5, vib=0.12, legato=0.85, s=0.7)
    s.line("lead2", "pulse", E, mel, 0, octave=1, gain=0.12, expect=48, duty=0.125, legato=0.7, s=0.5)
    s.arp("arp", "pulse", ch, 0, 4, 0.25, [0, 1, 2, 3, 4, 3, 2, 1], 64, gain=0.12, duty=0.25, legato=0.8,
          s=0.2, d=0.05)
    s.bass("bass", "tri", ch, 0, 4, [(k * 0.5, 0.45, "r" if k % 2 == 0 else "o") for k in range(8)], gain=0.7)
    s.drums("drums", 0, 12, 4, {"kick": [(0, 1.0), (1, 0.8), (2, 1.0), (3, 0.8)], "snare": [(1, 0.6), (3, 0.6)],
                                "hat": [(e * 0.25, 0.3 if e % 2 else 0.15) for e in range(16)]})
    for k in range(24):
        s.add("spark", "bell", 88 + (k * 5) % 12, k * 2 + 0.5, 0.25, 0.1, kind="glock", decay=0.3)
    s.hit("drums", s.kit["crash"], 0, 0.5)

    return master(s, {"lead": (1.0, 0.15, 0.15), "lead2": (1.0, 0.1, 0.1), "arp": (1.0, 0.15, 0.2),
                      "bass": (0.6, 0.05, 0), "drums": (0.8, 0.1, 0), "spark": (1.6, 0.4, 0.3)},
                  reverb=(1.0, 7000.0), echo=(0.75, 0.2, 2), rms_db=-17.5)


# --------------------------------------------------------------------------------------------
# Ambiences (22050 Hz, seamless: FFT-shaped noise, whole-cycle LFOs, wrapped events, circular reverb)
# --------------------------------------------------------------------------------------------


def _drip(rng, f0):
    n = int(0.25 * SR)
    t = np.arange(n) / SR
    fr = f0 * (1.0 + 0.7 * (1 - np.exp(-t / 0.012)))
    y = np.sin(2 * np.pi * np.cumsum(fr) / SR) * np.exp(-t / 0.045) * np.minimum(1, t / 0.001)
    return y


def cavern_ambience():
    dur = 30.0
    n = int(dur * SR)
    t = np.arange(n) / SR
    rng = np.random.default_rng(301)
    rumble = circ_noise(n, rng, lambda f: np.where(f < 15, 0, 1.0 / np.maximum(f, 20)) * np.exp(-f / 140.0))
    rumble *= 0.7 + 0.2 * loop_lfo(t, dur, 2) + 0.1 * loop_lfo(t, dur, 5, 1.0)
    air = circ_noise(n, rng, lambda f: np.exp(-((f - 320.0) / 160.0) ** 2))
    air *= 0.5 + 0.3 * loop_lfo(t, dur, 3, 2.0)
    y = 0.8 * rumble + 0.12 * air
    # Distant rock tumbles.
    for start, g in ((6.5, 0.7), (21.0, 0.5)):
        m = int(3.0 * SR)
        tt = np.arange(m) / SR
        burst = lowpass(rng.normal(0, 1, m), SR, 180) * np.sin(np.pi * tt / 3.0) ** 2 * g
        for k in range(10):
            pos = int((0.4 + 0.2 * k + rng.uniform(0, 0.1)) * SR)
            click = lowpass(rng.normal(0, 1, 600), SR, 900) * np.exp(-np.arange(600) / 120.0) * 0.4 * g
            burst[pos:pos + 600] += click[: max(0, min(600, m - pos))]
        place_wrap(y, burst * 1.5, int(start * SR))
    # Drips: three "stalactites" that each drip on their own slightly irregular period.
    wet = np.zeros(n)
    for f0, period, first, g in ((1650, 2.6, 0.4, 0.5), (2300, 3.7, 1.9, 0.35), (1250, 5.0, 3.1, 0.45)):
        k = 0
        while first + k * period < dur - 0.01:
            jitter = 0.08 * np.sin(k * 1.7 + f0)
            place_wrap(wet, g * _drip(rng, f0 * (1 + 0.03 * np.sin(k * 2.3))), int((first + k * period + jitter) * SR))
            k += 1
    wet = wet + 0.5 * conv_circular(wet, echo_ir(SR, 0.31, 0.4, 4, 3000))
    rev = conv_circular(0.35 * wet + 0.15 * y, reverb_ir(SR, 3.5, np.random.default_rng(31), 2500, 0.04))
    out = y + 0.7 * wet + 0.8 * rev
    out = hp_c(out, 25, 1)
    return finish(out, SR, -25.0, -9.0)


def grotto_ambience():
    dur = 28.0
    n = int(dur * SR)
    t = np.arange(n) / SR
    rng = np.random.default_rng(302)
    hum = circ_noise(n, rng, lambda f: np.where(f < 20, 0, 1.0 / np.sqrt(np.maximum(f, 20))) * np.exp(-f / 300.0))
    hum *= 0.7 + 0.2 * loop_lfo(t, dur, 2) + 0.1 * loop_lfo(t, dur, 7, 0.5)
    swish = circ_noise(n, rng, lambda f: np.exp(-((f - 600.0) / 300.0) ** 2))
    swish *= np.maximum(0, loop_lfo(t, dur, 4, 1.2)) ** 2 * 0.6 + 0.1
    y = 0.8 * hum + 0.15 * swish
    bub = np.zeros(n)

    def bubble(f0, length=0.05):
        m = int(length * SR)
        tt = np.arange(m) / SR
        fr = f0 * (1 + 1.6 * tt / length)
        return np.sin(2 * np.pi * np.cumsum(fr) / SR) * np.exp(-tt / (length * 0.45)) * np.minimum(1, tt / 0.002)

    for k in range(12):                       # bubble streams
        start = k * dur / 12 + rng.uniform(0, 1.2)
        count = int(rng.integers(5, 13))
        pos = start
        base = rng.uniform(450, 1100)
        for j in range(count):
            place_wrap(bub, bubble(base * rng.uniform(0.85, 1.25), rng.uniform(0.03, 0.07)) * rng.uniform(0.3, 0.8),
                       int(pos * SR))
            pos += rng.uniform(0.04, 0.13)
    for k in range(18):                       # loose single bubbles
        place_wrap(bub, bubble(rng.uniform(300, 1500), 0.06) * 0.35, int(rng.uniform(0, dur) * SR))
    # A distant, low creak of the ruins.
    m = int(4.0 * SR)
    tt = np.arange(m) / SR
    creak = np.sin(2 * np.pi * np.cumsum(140 - 30 * tt / 4.0) / SR) * np.sin(np.pi * tt / 4.0) ** 2 * 0.25
    place_wrap(y, creak, int(15.0 * SR))
    out = y + 0.5 * bub
    out = out + 0.7 * conv_circular(out, reverb_ir(SR, 2.5, np.random.default_rng(32), 1500, 0.03))
    out = lp_c(out, 2300)                     # muffled: everything heard through water
    out = hp_c(out, 25, 1)
    return finish(out, SR, -25.0, -9.0)


def canopy_wind():
    dur = 24.0
    n = int(dur * SR)
    t = np.arange(n) / SR
    rng = np.random.default_rng(303)
    gust = np.full(n, 0.25)
    for c, w, g in ((3.0, 3.0, 0.9), (9.5, 2.5, 0.6), (14.5, 4.0, 1.0), (20.5, 2.2, 0.7)):
        d = (t - c + dur / 2) % dur - dur / 2      # circular distance to the gust centre
        gust += g * np.where(np.abs(d) < w, 0.5 + 0.5 * np.cos(np.pi * d / w), 0.0)
    gust = lp_c(gust, 3, 1)
    body = circ_noise(n, rng, lambda f: np.where(f < 20, 0, 1.0 / np.sqrt(np.maximum(f, 20))) * np.exp(-f / 900.0))
    whistle_lo = circ_noise(n, rng, lambda f: np.exp(-((f - 620.0) / 60.0) ** 2))
    whistle_hi = circ_noise(n, rng, lambda f: np.exp(-((f - 910.0) / 80.0) ** 2))
    leaves = circ_noise(n, rng, lambda f: np.where(f > 2500, 1.0, 0.0) * np.exp(-f / 9000.0))
    flutter = np.abs(circ_noise(n, rng, lambda f: np.where(f < 25, 1.0, 0.0)))
    y = 0.6 * body * (0.3 + gust) + 0.12 * whistle_lo * gust ** 2 + 0.08 * whistle_hi * np.maximum(gust - 0.5, 0) ** 2
    y += 0.25 * leaves * flutter * (0.15 + gust) ** 1.5
    # Faint distant birds.
    birds = np.zeros(n)
    for start, base in ((2.0, 2900), (7.2, 3300), (12.0, 2600), (17.3, 3100), (22.1, 2800)):
        for j in range(3):
            m = int(0.09 * SR)
            tt = np.arange(m) / SR
            fr = base * (1.25 - 0.3 * tt / 0.09) * (1 + 0.02 * j)
            chirp = np.sin(2 * np.pi * np.cumsum(fr) / SR) * np.sin(np.pi * tt / 0.09) ** 1.5
            place_wrap(birds, chirp * 0.2, int((start + j * 0.16) * SR))
    # One creaking branch in the big gust.
    m = int(1.2 * SR)
    tt = np.arange(m) / SR
    creak = pulse(55 + 15 * np.sin(np.pi * tt / 1.2), m, SR, 0.1) * np.sin(np.pi * tt / 1.2) ** 2
    place_wrap(y, bandpass(creak, SR, 300, 1500) * 0.15, int(14.0 * SR))
    out = y + birds
    out = out + 0.4 * conv_circular(out, reverb_ir(SR, 1.5, np.random.default_rng(33), 5000, 0.02))
    out = hp_c(out, 30, 1)
    return finish(out, SR, -24.0, -9.0)


def hollow_ambience():
    dur = 30.0
    n = int(dur * SR)
    t = np.arange(n) / SR
    rng = np.random.default_rng(304)
    bed = circ_noise(n, rng, lambda f: np.where(f < 20, 0, 1.0 / np.maximum(f, 30) ** 0.8) * np.exp(-f / 700.0))
    bed *= 0.6 + 0.25 * loop_lfo(t, dur, 2, 0.3) + 0.15 * loop_lfo(t, dur, 5)
    y = 0.6 * bed
    # Moaning wind: narrow resonant bands, each swelling on its own whole-cycle LFO (dissonant set).
    for fc, cyc, ph in ((311.0, 3, 0.0), (330.0, 4, 1.4), (466.0, 5, 2.2), (494.0, 2, 4.0), (698.0, 7, 0.9)):
        band = circ_noise(n, rng, lambda f, fc=fc: np.exp(-((f - fc) / 6.0) ** 2))
        y += 0.13 * band * np.maximum(0, loop_lfo(t, dur, cyc, ph)) ** 3
    # Drone: E and F with whole-cycle frequencies (loop-continuous).
    for f0, g in ((82.41, 0.08), (87.31, 0.06), (123.47, 0.04)):
        fr = round(f0 * dur) / dur
        y += g * np.sin(2 * np.pi * fr * t) * (0.7 + 0.3 * loop_lfo(t, dur, 1, f0))
    # Distant moth wings: fluttering low noise bursts drifting past.
    for start, rate, length, g in ((4.0, 16.0, 2.2, 0.5), (13.5, 19.0, 1.6, 0.35), (22.0, 14.0, 2.6, 0.45)):
        m = int(length * SR)
        tt = np.arange(m) / SR
        flap = (0.5 + 0.5 * np.sin(2 * np.pi * rate * tt)) ** 3
        wing = lowpass(rng.normal(0, 1, m), SR, 500) * flap * np.sin(np.pi * tt / length) ** 2 * g
        place_wrap(y, wing, int(start * SR))
    # Faint, detuned glass tones far away.
    glass = np.zeros(n)
    for start, nm in ((2.5, "B5"), (10.0, "C6"), (18.5, "F5"), (26.0, "E5")):
        glass_note = v_bell(hz(midi(nm)), 0.5, SR, rng, kind="glass", decay=1.2, detune=25)
        place_wrap(glass, glass_note * 0.05, int(start * SR))
    out = y + glass
    out = out + 0.8 * conv_circular(out, reverb_ir(SR, 4.0, np.random.default_rng(34), 2500, 0.05))
    out = hp_c(out, 25, 1)
    return finish(out, SR, -25.0, -9.0)


# --------------------------------------------------------------------------------------------
# Catalogue + writer + verifier
# --------------------------------------------------------------------------------------------

TRACKS = [
    # name, builder, volume, bus, peak dBFS, tempo/key description
    ("Intro Theme", intro_theme, 0.45, "music", -6.0, "75 BPM 4/4, D major -> D minor -> F major -> D major"),
    ("Menu Theme", menu_theme, 0.45, "music", -6.0, "96 BPM 4/4, G major"),
    ("Overworld Theme", overworld_theme, 0.45, "music", -6.0, "120 BPM 4/4 march, F major (B: D minor)"),
    ("Village Theme", village_theme, 0.6, "music", -6.0, "138 BPM 3/4 waltz, Bb major (B: G minor)"),
    ("Cavern Theme", cavern_theme, 0.6, "music", -6.0, "84 BPM 4/4, A minor"),
    ("Grotto Theme", grotto_theme, 0.6, "music", -6.0, "88 BPM 4/4 (triplet arps), Eb lydian"),
    ("Canopy Theme", canopy_theme, 0.6, "music", -6.0, "116 BPM 4/4, A major / mixolydian"),
    ("Hollow Theme", hollow_theme, 0.6, "music", -6.0, "72 BPM 4/4, E minor (phrygian b2)"),
    ("Boss Theme", boss_theme, 0.6, "music", -6.0, "160 BPM 4/4, C minor"),
    ("Duskmantle Theme", duskmantle_theme, 0.6, "music", -6.0, "150 BPM 4/4, F minor (phase 3 F major motif)"),
    ("Credits Theme", credits_theme, 0.45, "music", -6.0, "72 BPM 4/4, C major"),
    ("Starlit Theme", starlit_theme, 0.6, "music", -6.0, "184 BPM 4/4, E major"),
    ("Cavern Ambience", cavern_ambience, 0.5, "ambient", -9.0, "drips, distant rumble"),
    ("Grotto Ambience", grotto_ambience, 0.5, "ambient", -9.0, "muffled underwater, bubbles"),
    ("Canopy Wind", canopy_wind, 0.5, "ambient", -9.0, "wind gusts, leaves, distant birds"),
    ("Hollow Ambience", hollow_ambience, 0.4, "ambient", -9.0, "eerie wind, moth wings, drone"),
]

MAX_BYTES = int(3.5 * 1024 * 1024)


def write_wav(name, x):
    data = np.clip(np.round(x * 32767.0), -32768, 32767).astype("<i2")
    path = os.path.join(OUT, name + ".wav")
    with wave.open(path, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(SR)
        w.writeframes(data.tobytes())
    return path


def render(names=None):
    os.makedirs(OUT, exist_ok=True)
    for name, fn, volume, bus, peak, desc in TRACKS:
        if names and name not in names:
            continue
        y = fn()
        y = y * (10 ** (peak / 20.0) / np.max(np.abs(y)))
        path = write_wav(name, y)
        ag.write_resource(name, True, bus, volume)
        print("%-17s %6.2f s  %8d bytes  bus=%-7s  %s" % (name, len(y) / SR, os.path.getsize(path), bus, desc))


def verify():
    ok = True
    print("%-17s %7s %9s %7s %7s %8s %8s  %s" % ("name", "secs", "bytes", "peak", "rms", "seam", "p99.9", "result"))
    for name, fn, volume, bus, peak, desc in TRACKS:
        path = os.path.join(OUT, name + ".wav")
        problems = []
        with wave.open(path, "rb") as w:
            if (w.getnchannels(), w.getsampwidth(), w.getframerate(), w.getcomptype()) != (1, 2, SR, "NONE"):
                problems.append("format %s" % ((w.getnchannels(), w.getsampwidth(), w.getframerate()),))
            x = np.frombuffer(w.readframes(w.getnframes()), "<i2").astype(float) / 32768.0
        size = os.path.getsize(path)
        secs = len(x) / SR
        with open(path, "rb") as fh:
            head = fh.read(44)
        if head[:4] != b"RIFF" or head[8:16] != b"WAVEfmt " or int.from_bytes(head[4:8], "little") != size - 8:
            problems.append("RIFF header")
        if size > MAX_BYTES:
            problems.append("size > 3.5 MiB")
        lo, hi = {"Intro Theme": (50, 70), "Credits Theme": (80, 120), "Starlit Theme": (10, 20)}.get(
            name, (40, 75) if bus == "music" else (15, 40))
        if not lo <= secs <= hi:
            problems.append("duration %.2f not in %g-%g" % (secs, lo, hi))
        pk = 20 * np.log10(np.max(np.abs(x)))
        rms = 20 * np.log10(np.sqrt(np.mean(x ** 2)))
        if abs(pk - peak) > 0.1:
            problems.append("peak %.2f" % pk)
        # Seam: the step from the last sample back to the first must look like any other step.
        steps = np.abs(np.diff(x))
        seam = abs(x[0] - x[-1])
        p999 = np.percentile(steps, 99.9)
        if seam > p999:
            problems.append("seam step %.4f > p99.9 %.4f" % (seam, p999))
        # No level jump either: compare the RMS of the 0.3 s either side of the seam.
        k = int(0.3 * SR)
        r_end = np.sqrt(np.mean(x[-k:] ** 2))
        r_start = np.sqrt(np.mean(x[:k] ** 2))
        if max(r_end, r_start) / max(min(r_end, r_start), 1e-9) > 4.0:
            problems.append("level jump at seam %.3f vs %.3f" % (r_end, r_start))
        with open(os.path.join(OUT, name + ".audio.json"), encoding="utf-8") as fh:
            doc = json.load(fh)
        if doc.get("loop") is not True or doc.get("bus") != bus or doc.get("source") != "Assets/Audio/%s.wav" % name:
            problems.append("audio.json")
        with open(os.path.join(OUT, name + ".audio.json.meta"), encoding="utf-8") as fh:
            meta = json.load(fh)
        if meta.get("kind") != "Audio" or meta.get("guid") != ag.guid_for(name):
            problems.append("meta")
        ok = ok and not problems
        print("%-17s %7.2f %9d %7.2f %7.2f %8.4f %8.4f  %s" % (name, secs, size, pk, rms, seam, p999,
                                                               "OK" if not problems else "; ".join(problems)))
    print("ALL OK" if ok else "PROBLEMS FOUND")
    return ok


if __name__ == "__main__":
    args = sys.argv[1:]
    if args == ["--verify"]:
        sys.exit(0 if verify() else 1)
    render(set(args) if args else None)
