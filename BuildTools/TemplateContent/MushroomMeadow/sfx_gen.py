"""Mushroom Meadow (full game) sound-effect generator.

The effects share the game's own soft, woodland palette: kalimba tines, marimba bars, glass rods, wood
blocks and singing bowls (modal synthesis), plucked strings (Karplus-Strong), a breathy wood flute, FM
and filtered noise for whooshes, puffs, splashes and rustles. Reward jingles quote the music's
Glowspore motif (sol-do-re-mi). There are deliberately no pulse-wave console blips or
classic-platformer jingles.

Deterministic: re-running produces byte-identical WAVs. Every sound is 44.1 kHz, 16-bit, mono,
normalised to a -3 dBFS peak, with a faded-in first sample and a faded-out last sample (click free).
Writes into Source/Genesis.Application.Core/Projects/Templates/Assets/TwoDShowcase/Assets/Audio the
.wav, its .audio.json resource document (bus "sfx", loop false) and .audio.json.meta (kind "Audio").

Reuses the helpers and organic instruments in audio_gen.py (imported; phase-1 outputs are not touched).

Usage:  python BuildTools/TemplateContent/MushroomMeadow/sfx_gen.py
Requires numpy + scipy.
"""

import hashlib
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from audio_gen import (OUT, as_array, bandpass, echo, env_adsr, env_exp, fade_tail, flute,  # noqa: E402
                       highpass, ks_pluck, lowpass, nhz, noise, phrase, place, saw_wave, shaker, sine,
                       struck, sweep, up, write_resource, write_wav)
from audio_gen import chimes as chime_shower  # noqa: E402

SR = 44100


# --------------------------------------------------------------------------------------------
# Small helpers
# --------------------------------------------------------------------------------------------

def secs(d):
    return int(d * SR)


def freq_of(note):
    return nhz(note) if isinstance(note, str) else float(note)


def mix(*parts):
    """parts: (array, start_seconds[, gain]). Returns a buffer long enough for all of them."""
    n = max(secs(p[1]) + len(p[0]) for p in parts)
    buf = np.zeros(n)
    for p in parts:
        g = p[2] if len(p) > 2 else 1.0
        place(buf, g * p[0], secs(p[1]))
    return buf


def bell(note, dur, decay=0.5, bright=1.0):
    """Soft additive bell: slightly inharmonic partials, upper ones die faster."""
    f = freq_of(note)
    n = secs(dur)
    y = np.zeros(n)
    for ratio, g, dk in ((1.0, 1.0, 1.0), (2.0, 0.45, 0.6), (3.01, 0.22 * bright, 0.4),
                         (4.16, 0.14 * bright, 0.28), (5.43, 0.08 * bright, 0.2)):
        if f * ratio < SR * 0.45:
            y += g * sine(f * ratio, n, SR) * env_exp(n, SR, decay * dk, 0.002)
    return fade_tail(y, SR, 0.01)


def fm(carrier, ratio, index, n):
    """Two-operator FM: carrier frequency (array or scalar), modulator = carrier * ratio."""
    fc = as_array(carrier, n)
    ph_c = np.concatenate(([0.0], np.cumsum(fc[:-1]))) / SR
    ph_m = ph_c * ratio
    return np.sin(2 * np.pi * ph_c + as_array(index, n) * np.sin(2 * np.pi * ph_m))


def svf(x, fc, q=1.0, mode="band"):
    """Chamberlin state-variable filter with a per-sample cutoff (for sweeping whooshes)."""
    fc = as_array(fc, len(x))
    f = 2.0 * np.sin(np.pi * np.clip(fc, 20.0, SR / 6.5) / SR)
    damp = 1.0 / q
    low = band = 0.0
    out = np.empty(len(x))
    want_low = mode == "low"
    for i in range(len(x)):
        low += f[i] * band
        high = x[i] - low - damp * band
        band += f[i] * high
        out[i] = low if want_low else band
    return out


def saturate(x, drive):
    return np.tanh(x * drive) / np.tanh(drive)


def bell_curve(n, peak, width):
    t = np.linspace(0.0, 1.0, n)
    return np.exp(-((t - peak) / width) ** 2)


def raised(n, rise, total):
    """Smooth swell: raised-cosine rise over `rise` of the length, raised-cosine fall over the rest."""
    t = np.linspace(0.0, 1.0, n)
    up_ = 0.5 - 0.5 * np.cos(np.pi * np.clip(t / rise, 0, 1))
    down = 0.5 + 0.5 * np.cos(np.pi * np.clip((t - rise) / (total - rise), 0, 1))
    return np.where(t < rise, up_, down)


def bubble(f0, dur, gain=1.0):
    n = secs(dur)
    f = sweep(f0, f0 * 2.3, n)
    e = np.sin(np.pi * np.linspace(0, 1, n)) ** 0.7 * np.exp(-np.linspace(0, 3, n))
    return gain * sine(f, n, SR) * e


def grain(rng, dur, lo, hi):
    n = max(8, secs(dur))
    return bandpass(noise(n, rng), SR, lo, hi) * env_exp(n, SR, dur * 0.35, 0.0008)


def thump(f0, f1, dur, decay, attack=0.001):
    n = secs(dur)
    return sine(sweep(f0, f1, n), n, SR) * env_exp(n, SR, decay, attack)


# --------------------------------------------------------------------------------------------
# Hero / power-ups
# --------------------------------------------------------------------------------------------

def sfx_spore_throw(rng):
    """Seed-pod flick: a short breath 'fwip' (noise band lifting 900 -> 3800 Hz) and a hollow wooden
    'pok' as the spore leaves Pip's hand. No pitched squeak."""
    n = secs(0.17)
    air = svf(noise(n, rng), sweep(900, 3800, n), q=1.5) * env_adsr(n, SR, 0.01, 0.05, 0.3, 0.07)
    pok = struck("wood", 760, 0.08, SR, 0.018, rng, mallet=0.3)
    y = mix((air, 0, 1.0), (pok, 0.005, 0.45))
    return echo(fade_tail(lowpass(y, SR, 9000), SR), SR, 0.05, 0.12, taps=2)


def sfx_spore_hit(rng):
    """Soft 'poff' of a spore bursting: a cushioned membrane thump, a puff of dust and two glass motes."""
    n = secs(0.24)
    puff = lowpass(noise(n, rng, 2), SR, 2400) * env_exp(n, SR, 0.045, 0.001)
    body = thump(260, 120, 0.24, 0.05, 0.002)
    motes = [struck("glass", nm, 0.25, SR, 0.07, rng, mallet=0.0) for nm in ("E7", "A7")]
    y = mix((puff, 0, 0.9), (body, 0, 0.7), (motes[0], 0.05, 0.16), (motes[1], 0.1, 0.12))
    return echo(fade_tail(lowpass(y, SR, 9000), SR), SR, 0.06, 0.15, taps=2)


def sfx_power_up(rng):
    """Growth: leaves rustle up into a bowed-glass swell that brightens (its pitch never steps), a
    kalimba strum spread across C pentatonic, and a warm singing-bowl bloom."""
    n = secs(1.0)
    t = np.linspace(0.0, 1.0, n)
    pad = np.zeros(n)
    for nm, g, grow in (("C5", 0.6, 0.0), ("G5", 0.4, 0.6), ("C6", 0.3, 1.2), ("E6", 0.2, 1.8)):
        f = nhz(nm)
        pad += g * 0.5 * (sine(f, n, SR) + sine(f * 1.003, n, SR)) * t ** grow
    pad *= raised(n, 0.5, 1.0)
    parts = [(pad, 0.0, 0.35)]
    for k in range(18):
        t0 = 0.3 * (k / 18.0) ** 0.8 + rng.uniform(0, 0.02)
        parts.append((grain(rng, rng.uniform(0.01, 0.03), 2500, 8000), t0, 0.12 + 0.18 * k / 18.0))
    for k, nm in enumerate(("C6", "G6", "D6", "A6", "E7")):
        parts.append((struck("kalimba", nm, 0.9, SR, 0.3, rng, mallet=0.3), 0.32 + 0.035 * k, 0.4))
    parts.append((struck("bowl", "C5", 0.9, SR, 0.35, rng, mallet=0.05, detune=4.0), 0.45, 0.35))
    parts.append((struck("glass", "E7", 0.8, SR, 0.3, rng, mallet=0.0), 0.5, 0.18))
    y = mix(*parts)
    y = lowpass(y, SR, 10000)
    return echo(fade_tail(y, SR), SR, 0.09, 0.22, taps=3)


def sfx_power_down(rng):
    """Wilting: a soft wood knock, then a glass dyad that droops 1.5 semitones and darkens while an
    airy sigh deflates beneath it."""
    knock = struck("wood", 300, 0.12, SR, 0.03, rng, mallet=0.4)
    n = secs(0.85)
    droop = np.concatenate((np.ones(secs(0.12)), sweep(1.0, 2 ** (-1.5 / 12), n - secs(0.12))))
    dyad = struck("glass", "E6", 0.85, SR, 0.35, rng, mallet=0.1, bend=droop) \
        + 0.8 * struck("glass", "B5", 0.85, SR, 0.35, rng, mallet=0.1, bend=droop)
    dyad = svf(dyad, sweep(6000, 900, n), q=0.7, mode="low")
    sigh = svf(noise(n, rng), sweep(1800, 350, n), q=1.0, mode="low") * raised(n, 0.15, 1.0)
    y = mix((knock, 0, 0.6), (dyad, 0.02, 0.6), (sigh, 0.04, 0.5))
    return echo(fade_tail(y, SR), SR, 0.08, 0.18, taps=2)


def sfx_shield_break(rng):
    """Bark ring splintering: sharp crack, woody clonks, scattering chips and a snapped-fibre twang."""
    crack = highpass(noise(secs(0.09), rng), SR, 1300) * env_exp(secs(0.09), SR, 0.016, 0.0005)
    parts = [(crack, 0, 1.0)]
    for f, t0, g in ((430, 0.0, 1.0), (310, 0.035, 0.8), (540, 0.075, 0.6), (265, 0.12, 0.6),
                     (390, 0.19, 0.45), (300, 0.27, 0.32)):
        parts.append((struck("wood", f, 0.12, SR, 0.035, rng, mallet=0.2), t0, 0.55 * g))
    for k in range(14):
        t0 = 0.05 + 0.45 * (k / 14.0) ** 1.3 + rng.uniform(0, 0.02)
        parts.append((grain(rng, rng.uniform(0.01, 0.025), 2000, 6000), t0, 0.35 * (1 - k / 16.0)))
    parts.append((thump(115, 55, 0.14, 0.05), 0, 0.8))
    parts.append((ks_pluck("A3", 0.4, SR, rng, t60=0.35, bright=0.8), 0.04, 0.3))
    y = mix(*parts)
    return echo(fade_tail(lowpass(y, SR, 9000), SR), SR, 0.07, 0.22, taps=2)


def sfx_heart_get(rng):
    """Two soft heartbeats, then a warm bloom: a marimba E5 + B5, a kalimba G#6, a breathy flute E6
    and a glass shimmer, all opening together (no arpeggio)."""
    parts = [(thump(95, 55, 0.12, 0.04, 0.003), 0.0, 1.0), (thump(95, 55, 0.12, 0.04, 0.003), 0.14, 0.75)]
    warm = mix((struck("marimba", "E5", 1.0, SR, 0.4, rng, mallet=0.2), 0, 1.0),
               (struck("marimba", "B5", 1.0, SR, 0.35, rng, mallet=0.2), 0.02, 0.7))
    parts.append((warm, 0.28, 0.45))
    parts.append((struck("kalimba", "G#6", 0.8, SR, 0.3, rng, mallet=0.3), 0.3, 0.3))
    parts.append((flute("E6", 0.6, SR, rng, a=0.12, r=0.3, s=0.7, vib=0.2, breath=0.15), 0.32, 0.3))
    parts.append((struck("glass", "E7", 0.9, SR, 0.35, rng, mallet=0.0), 0.4, 0.2))
    y = mix(*parts)
    y = lowpass(y, SR, 9500)
    return echo(fade_tail(y, SR), SR, 0.1, 0.25, taps=3)


def sfx_glide_open(rng):
    """Dandelion parasol popping open: soft whoomp, fabric flutter, a floaty breath-flute whistle."""
    n = secs(0.6)
    t = np.arange(n) / SR
    whoosh = svf(noise(n, rng), np.concatenate((sweep(500, 3200, secs(0.16)), sweep(3200, 1400, n - secs(0.16)))),
                 q=1.2) * env_adsr(n, SR, 0.03, 0.1, 0.25, 0.2)
    flutter = lowpass(noise(n, rng), SR, 2600) * (0.5 + 0.5 * np.sign(np.sin(2 * np.pi * 24 * t))) \
        * env_exp(n, SR, 0.12, 0.08)
    whistle = flute(sweep(nhz("G5"), nhz("D6"), n), 0, SR, rng, a=0.08, r=0.25, s=0.5, vib=0.3, breath=0.4)
    pop = thump(240, 140, 0.05, 0.015)
    y = mix((whoosh, 0, 0.9), (flutter, 0.02, 0.25), (whistle, 0.04, 0.3), (pop, 0.0, 0.6))
    return echo(fade_tail(lowpass(y, SR, 8000), SR), SR, 0.08, 0.2, taps=2)


def sfx_swim_stroke(rng):
    """Muffled underwater push plus a few bloops of bubbles."""
    n = secs(0.3)
    push = lowpass(noise(n, rng), SR, 700) * env_adsr(n, SR, 0.035, 0.08, 0.2, 0.12)
    thrum = sine(sweep(140, 90, n), n, SR) * env_adsr(n, SR, 0.03, 0.08, 0.2, 0.1)
    parts = [(push, 0, 1.0), (thrum, 0, 0.4)]
    for t0, f0, g in ((0.06, 340, 0.5), (0.13, 520, 0.4), (0.18, 420, 0.35), (0.24, 650, 0.25)):
        parts.append((bubble(f0 * rng.uniform(0.95, 1.05), 0.045), t0, g))
    y = mix(*parts)
    return echo(fade_tail(lowpass(y, SR, 3500), SR), SR, 0.07, 0.18, taps=2)


def sfx_splash(rng):
    """Breaking the water surface: smack, washing hiss, droplets falling back."""
    n = secs(0.6)
    smack = lowpass(noise(n, rng, 2), SR, 2800) * env_exp(n, SR, 0.06, 0.001)
    wash = svf(noise(n, rng), sweep(5000, 900, n), q=0.8, mode="low") * env_adsr(n, SR, 0.01, 0.12, 0.3, 0.3)
    low = sine(sweep(160, 70, secs(0.15)), secs(0.15), SR) * env_exp(secs(0.15), SR, 0.05)
    parts = [(smack, 0, 0.8), (wash, 0, 0.7), (low, 0, 0.6)]
    for k in range(9):
        t0 = 0.12 + 0.55 * (k / 9.0) + rng.uniform(0, 0.04)
        parts.append((bubble(rng.uniform(1100, 2600), rng.uniform(0.02, 0.04)), t0, 0.35 * (1 - k / 11.0)))
    y = mix(*parts)
    return echo(fade_tail(lowpass(y, SR, 9000), SR), SR, 0.06, 0.2, taps=2)


# --------------------------------------------------------------------------------------------
# World / hazards / enemies
# --------------------------------------------------------------------------------------------

def sfx_crumble(rng):
    """Rock block giving way: crack, crumbling gravel grains, low rumble and earthy thuds."""
    n = secs(0.75)
    rumble = lowpass(noise(n, rng, 3), SR, 260) * env_adsr(n, SR, 0.02, 0.2, 0.5, 0.35)
    crack = highpass(noise(secs(0.06), rng), SR, 900) * env_exp(secs(0.06), SR, 0.012, 0.0005)
    parts = [(rumble, 0, 1.6), (crack, 0, 0.8)]
    for k in range(60):
        t0 = 0.02 + 0.6 * (k / 60.0) ** 1.4 + rng.uniform(0, 0.015)
        lo = rng.uniform(300, 1500)
        parts.append((grain(rng, rng.uniform(0.008, 0.03), lo, lo * 2.5), t0, rng.uniform(0.3, 0.8) * (1 - k / 75.0)))
    for t0, f in ((0.0, 120), (0.22, 95), (0.4, 80)):
        parts.append((thump(f, f * 0.6, 0.12, 0.04, 0.002), t0, 0.5))
    y = mix(*parts)
    return fade_tail(lowpass(y, SR, 7000), SR, 0.03)


def sfx_wind_gust(rng):
    """One-shot gust (~1.9 s): a whistling swell of wind with leaves rustling at the peak."""
    n = secs(1.9)
    t = np.linspace(0.0, 1.0, n)
    swell = raised(n, 0.38, 1.0)
    centre = 320 + 650 * swell + 120 * np.sin(2 * np.pi * 3.0 * t)
    body = svf(noise(n, rng), centre, q=2.2) * swell
    breath = svf(noise(n, rng), centre * 2.6, q=0.9) * swell ** 1.5
    whistle = sine(centre * 2.0, n, SR) * swell ** 2 * (0.6 + 0.4 * np.sin(2 * np.pi * 5 * t))
    rustle = np.zeros(n)
    for k in range(40):
        t0 = rng.uniform(0.35, 1.35)
        g = np.exp(-((t0 - 0.85) / 0.35) ** 2)
        place(rustle, grain(rng, rng.uniform(0.01, 0.03), 3500, 8500) * g * rng.uniform(0.4, 1.0), secs(t0))
    y = body + 0.35 * breath + 0.05 * whistle + 0.12 * rustle
    return lowpass(y, SR, 9000)


def sfx_bat_screech(rng):
    """Three squeaky cave-bat chirps (sine + FM squeak) with a cavern echo."""
    parts = []
    for i, (t0, dur, f0, fpk, f1) in enumerate(((0.0, 0.07, 3000, 4300, 2700), (0.1, 0.06, 3300, 4600, 3000),
                                                (0.19, 0.12, 2800, 4400, 2200))):
        n = secs(dur)
        t = np.linspace(0, 1, n)
        f = np.where(t < 0.35, f0 + (fpk - f0) * (t / 0.35), fpk + (f1 - fpk) * ((t - 0.35) / 0.65))
        f = f * (1 + 0.04 * np.sin(2 * np.pi * 70 * np.arange(n) / SR))
        y = 0.6 * sine(f, n, SR) + 0.35 * fm(f, 1.5, 0.8, n)
        parts.append((y * env_adsr(n, SR, 0.004, dur * 0.4, 0.6, dur * 0.3), t0, 1.0 if i != 1 else 0.8))
    y = mix(*parts)
    y = lowpass(y, SR, 9500)
    return echo(fade_tail(y, SR), SR, 0.09, 0.35, taps=4, damp=3500)


def sfx_moth_swoop(rng):
    """Dusk moth diving past: dusty wingbeat flutter riding a doppler whoosh."""
    n = secs(0.9)
    t = np.arange(n) / SR
    shape = bell_curve(n, 0.45, 0.25)
    centre = 500 + 1800 * bell_curve(n, 0.42, 0.22)
    whoosh = svf(noise(n, rng), centre, q=1.4) * shape
    beats = 0.5 + 0.5 * np.sin(2 * np.pi * 15 * t) ** 3
    flutter = lowpass(noise(n, rng, 2), SR, 1600) * np.clip(beats, 0, 1) * shape
    eerie = sine(sweep(300, 190, n), n, SR) * shape * 0.5
    y = whoosh + 0.7 * flutter + 0.12 * eerie
    y = y * np.minimum(1.0, t / 0.02)
    return echo(fade_tail(lowpass(y, SR, 7000), SR), SR, 0.1, 0.2, taps=2)


def sfx_orb_fire(rng):
    """Gloom Orb launched: dark purple FM 'vwomm' with a chorused tail and a faint glitter."""
    n = secs(0.6)
    f = sweep(330, 170, n)
    idx = 3.5 * np.exp(-np.arange(n) / (0.15 * SR)) + 0.6
    y = fm(f, 1.5, idx, n) + 0.7 * fm(f * 1.006, 1.5, idx * 0.9, n)
    y *= env_adsr(n, SR, 0.015, 0.15, 0.45, 0.2)
    sub = sine(f * 0.5, n, SR) * env_exp(n, SR, 0.2, 0.02)
    glitter = highpass(noise(secs(0.15), rng), SR, 6000) * env_exp(secs(0.15), SR, 0.04)
    out = mix((y, 0, 0.6), (sub, 0, 0.5), (glitter, 0.01, 0.2))
    out = lowpass(out, SR, 6000)
    return echo(fade_tail(out, SR), SR, 0.11, 0.3, taps=3, damp=3000)


def sfx_pebble_fall(rng):
    """A small stone landing and skittering: three ever-smaller clicks."""
    parts = []
    for t0, g in ((0.0, 1.0), (0.13, 0.55), (0.22, 0.35), (0.28, 0.2)):
        m = secs(0.04)
        tick = bandpass(noise(m, rng), SR, 1800, 6500) * env_exp(m, SR, 0.006, 0.0004)
        ping = sine(rng.uniform(1800, 2600), m, SR) * env_exp(m, SR, 0.012, 0.0005)
        parts.append((tick + 0.4 * ping, t0, g))
    m = secs(0.08)
    parts.append((sine(sweep(170, 90, m), m, SR) * env_exp(m, SR, 0.025), 0, 0.5))
    y = mix(*parts)
    return echo(fade_tail(y, SR), SR, 0.06, 0.15, taps=2)


# --------------------------------------------------------------------------------------------
# Bosses
# --------------------------------------------------------------------------------------------

def sfx_boss_hit(rng):
    """Heavy, satisfying hit on a boss: deep thump, metallic clang, crack and a hollow-log 'bonk'."""
    n = secs(0.25)
    deep = saturate(sine(sweep(150, 42, n), n, SR) * env_exp(n, SR, 0.09, 0.001), 2.0)
    clang = mix((bell(330, 0.55, 0.22, bright=1.6), 0), (bell(497, 0.45, 0.15, bright=1.4), 0, 0.6))
    crack = highpass(noise(secs(0.06), rng), SR, 1500) * env_exp(secs(0.06), SR, 0.012, 0.0004)
    bonk = struck("wood", 240, 0.3, SR, 0.08, rng, mallet=0.4, bend=sweep(1.0, 0.8, secs(0.3)))
    y = mix((deep, 0, 1.0), (clang, 0, 0.35), (crack, 0, 0.7), (bonk, 0.005, 0.45))
    y = lowpass(y, SR, 9000)
    return echo(fade_tail(y, SR), SR, 0.1, 0.25, taps=3)


def sfx_boss_roar(rng):
    """Gritty layered roar with a long pitch drop (Grumblecrag / Duskmantle)."""
    n = secs(1.7)
    t = np.arange(n) / SR
    tn = t / t[-1]
    f = np.where(tn < 0.15, 140 + 40 * (tn / 0.15), 180 * (55 / 180) ** ((tn - 0.15) / 0.85))
    f = f * (1 + 0.07 * np.sin(2 * np.pi * 31 * t) + 0.03 * lowpass(noise(n, rng, 40), SR, 60) * 8)
    growl = 0.55 * saw_wave(f, n, SR) + 0.45 * saw_wave(f * 1.013, n, SR)
    growl *= 0.75 + 0.25 * lowpass(noise(n, rng, 50), SR, 80) * 6
    sub = sine(f * 0.5, n, SR)
    breath = svf(noise(n, rng), 1800 * (1 - 0.6 * tn), q=0.7)
    env = env_adsr(n, SR, 0.07, 0.4, 0.7, 0.5)
    y = (growl + 0.5 * sub + 0.9 * breath) * env
    y = saturate(y, 3.0)
    y = svf(y, 4200 * (1 - 0.75 * tn) + 400, q=0.8, mode="low")
    return echo(fade_tail(y, SR, 0.05), SR, 0.13, 0.28, taps=3, damp=2500)


def sfx_boss_charge(rng):
    """Boss winding up a charge: rising rumble and growling whine with a quickening tremolo, then a lunge."""
    n = secs(1.05)
    t = np.arange(n) / SR
    tn = t / t[-1]
    rumble = lowpass(noise(n, rng, 2), SR, 240) * (0.3 + 0.7 * tn)
    trem_rate = 6 + 18 * tn
    trem_ph = np.cumsum(trem_rate) / SR
    trem = 0.6 + 0.4 * np.sin(2 * np.pi * trem_ph)
    whine = saw_wave(sweep(85, 480, n), n, SR) * trem * (0.2 + 0.8 * tn ** 1.5)
    whine = lowpass(whine, SR, 3000)
    y = 1.6 * rumble + 0.6 * whine
    y *= np.minimum(1.0, t / 0.05) * np.minimum(1.0, (t[-1] - t) / 0.06 + 0.0)
    m = secs(0.3)
    lunge = svf(noise(m, rng), sweep(3000, 700, m), q=1.0) * env_exp(m, SR, 0.08, 0.004)
    out = mix((y, 0, 1.0), (lunge, 0.98, 0.9))
    return fade_tail(lowpass(out, SR, 8000), SR, 0.02)


def sfx_boss_defeated(rng):
    """Boss collapses: chain of booms and a long falling wind-howl, resolving into a soft light swell."""
    parts = []
    for t0, g, f in ((0.0, 1.0, 110), (0.24, 0.8, 95), (0.5, 0.75, 120), (0.82, 0.65, 85), (1.18, 0.55, 70)):
        m = secs(0.45)
        boom = lowpass(noise(m, rng, 3), SR, 900) * env_exp(m, SR, 0.12, 0.002)
        boom += 1.2 * sine(sweep(f, f * 0.45, m), m, SR) * env_exp(m, SR, 0.14, 0.002)
        parts.append((saturate(boom, 1.6), t0, g))
    m = secs(1.4)
    tt = np.arange(m) / SR
    centre = sweep(1100, 120, m) * (1 + 0.04 * np.sin(2 * np.pi * 9 * tt))
    howl = svf(noise(m, rng), centre, q=6.0) + 0.25 * sine(centre, m, SR)
    parts.append((howl * env_adsr(m, SR, 0.05, 0.6, 0.5, 0.3), 0.05, 0.35))
    m = secs(1.5)
    glow = np.zeros(m)
    for nm, g in (("C6", 0.5), ("E6", 0.4), ("G6", 0.4), ("C7", 0.25)):
        glow += g * sine(nhz(nm), m, SR) * (0.8 + 0.2 * np.sin(2 * np.pi * 7 * np.arange(m) / SR))
    glow *= raised(m, 0.35, 1.0)
    parts.append((glow, 1.35, 0.35))
    for k, nm in enumerate(("G6", "C7", "E7", "G7")):
        parts.append((struck("glass", nm, 0.6, SR, 0.22, rng, mallet=0.0), 1.55 + 0.09 * k, 0.18))
    y = mix(*parts)
    y = lowpass(y, SR, 8500)
    return echo(fade_tail(y, SR, 0.05), SR, 0.15, 0.3, taps=4)


# --------------------------------------------------------------------------------------------
# Rewards / jingles
# --------------------------------------------------------------------------------------------

def sfx_glowspore_get(rng):
    """Glowspore found (~3 s): a plucked-harp sweep up C-major pentatonic (the Finish Fanfare's key,
    which it can overlap), the Glowspore motif (sol-do-re-mi-- sol) on breathy flute doubled by
    kalimba over marimba, then a shimmering glass pad with fireflies of glass chimes."""
    bpm = 132
    beat = 60.0 / bpm
    parts = []
    for k, nm in enumerate(("C4", "E4", "G4", "C5", "D5", "E5", "G5", "C6")):
        parts.append((ks_pluck(nm, 1.2, SR, rng, t60=1.0, bright=0.6), 0.04 * k, 0.22))
    t0 = 0.36
    for nm, s, d in phrase([("G5", .5), ("C6", .5), ("D6", .5), ("E6", 1.0), ("G6", 2.0)], bpm, t0):
        parts.append((flute(nm, d * 0.97 + 0.06, SR, rng, a=0.03, r=0.15, vib=0.2, breath=0.1), s, 0.42))
        parts.append((struck("kalimba", nm, min(1.0, d + 0.4), SR, 0.25, rng, mallet=0.3), s, 0.3))
    for nm, s, d in phrase([("C3", 1.0), ("G2", 1.0), ("C3", 2.5)], bpm, t0):
        parts.append((struck("marimba", nm, d + 0.4, SR, 0.5, rng, mallet=0.2), s, 0.45))
        parts.append((struck("marimba", up(nm, 7), d + 0.3, SR, 0.4, rng, mallet=0.2), s + 0.02, 0.25))
    hold_start = t0 + 2.5 * beat
    m = secs(2.0 * beat + 0.3)
    tt = np.arange(m) / SR
    pad = np.zeros(m)
    for nm, g in (("C6", 0.5), ("E6", 0.4), ("G6", 0.4), ("C7", 0.25)):
        f = nhz(nm)
        pad += g * (sine(f, m, SR) + sine(f * 1.004, m, SR)) * 0.5
    pad *= (0.75 + 0.25 * np.sin(2 * np.pi * 6 * tt)) * raised(m, 0.25, 1.0)
    parts.append((pad, hold_start, 0.22))
    parts += chime_shower(rng, SR, ["C7", "D7", "E7", "G7", "G6"], hold_start, 12, gap=0.07, gain=0.16,
                          decay=0.12)
    y = mix(*parts)
    y = lowpass(y, SR, 10000)
    return echo(fade_tail(y, SR, 0.05), SR, 0.13, 0.25, taps=3)


def sfx_level_clear(rng):
    """Cozy marimba jig in F (~3.8 s): the Glowspore motif opening (sol-do-re-mi) and a pentatonic
    answer on marimba + kalimba, plucked-string offbeat chords, wood-block and shaker percussion,
    closing on a rolled harp chord and a wind-chime shower. No drum kit, no crash."""
    bpm = 150
    beat = 60.0 / bpm
    lead = [("C5", .5), ("F5", .5), ("G5", .5), ("A5", .5), ("C6", 1.0), ("A5", .5), ("G5", .5),
            ("F5", .5), ("G5", .5), ("A5", .5), ("D6", .5), ("C6", .5), ("A5", .5), ("F5", 2.0)]
    parts = []
    for nm, s, d in phrase(lead, bpm):
        parts.append((struck("marimba", nm, min(1.2, d + 0.5), SR, 0.3, rng, mallet=0.25), s, 0.5))
        parts.append((struck("kalimba", up(nm, 12), min(0.9, d + 0.3), SR, 0.18, rng, mallet=0.25), s, 0.18))
    chords = {"F": ("F3", "A3", "C4"), "Bb": ("F3", "Bb3", "D4"), "C": ("E3", "G3", "C4")}
    roots = {"F": "F2", "Bb": "Bb2", "C": "C3"}
    for b, ch in enumerate(("F", "F", "F", "F", "Bb", "Bb", "C", "C")):
        parts.append((struck("marimba", roots[ch], 0.6, SR, 0.3, rng, mallet=0.2), b * beat, 0.4))
        for k, nm in enumerate(chords[ch]):
            parts.append((ks_pluck(nm, 0.35, SR, rng, t60=0.25, bright=0.5), (b + 0.5) * beat + 0.012 * k, 0.12))
        if b % 2 == 1:
            parts.append((struck("wood", 1100, 0.06, SR, 0.015, rng, mallet=0.25), b * beat, 0.16))
    for e in range(16):
        parts.append((shaker(rng, SR), e * 0.5 * beat, 0.04 if e % 2 else 0.07))
    end = 7.0 * beat
    parts.append((struck("marimba", "F2", 1.6, SR, 0.6, rng, mallet=0.2), end, 0.45))
    for k, nm in enumerate(("F3", "C4", "F4", "A4", "C5", "F5")):
        parts.append((ks_pluck(nm, 1.6, SR, rng, t60=0.9, bright=0.55), end + 0.028 * k, 0.15))
    parts.append((struck("bowl", "F4", 1.3, SR, 0.4, rng, mallet=0.05, detune=4.0), end, 0.22))
    parts += chime_shower(rng, SR, ["C7", "D7", "F7", "G7", "A7"], end + 0.25, 7, gap=0.1, gain=0.14)
    y = mix(*parts)
    y = lowpass(y, SR, 10000)
    return echo(fade_tail(y, SR, 0.05), SR, 0.12, 0.22, taps=3)


def sfx_starlit_get(rng):
    """Starlit Firefly: a swarm of tiny glass chimes igniting (C pentatonic, a deterministic scatter
    that thickens then thins) over a bowed-glass swell and a fluttering breath. No glissando or trill."""
    penta = ["C7", "D7", "E7", "G7", "A7", "C8"]
    parts = [(struck("kalimba", "C6", 0.9, SR, 0.3, rng, mallet=0.3), 0.0, 0.45)]
    t0 = 0.0
    for k in range(22):
        nm = penta[int(rng.integers(0, len(penta)))]
        g = 0.28 * (0.4 + 0.6 * np.sin(np.pi * k / 21.0))
        parts.append((struck("glass", nm, 0.4, SR, 0.1, rng, mallet=0.0), t0, g))
        t0 += 0.025 + 0.04 * abs(k - 8) / 14.0 + 0.01 * rng.uniform()
    n = secs(1.3)
    t = np.arange(n) / SR
    pad = np.zeros(n)
    for nm, g in (("C6", 0.5), ("E6", 0.35), ("G6", 0.35), ("C7", 0.2)):
        f = nhz(nm)
        pad += g * 0.5 * (sine(f, n, SR) + sine(f * 1.004, n, SR))
    pad *= raised(n, 0.3, 1.0)
    breath = flute("G6", 0.9, SR, rng, a=0.1, r=0.4, s=0.6, vib=0.0, breath=0.5)
    breath *= 0.6 + 0.4 * np.sin(2 * np.pi * 18 * np.arange(len(breath)) / SR)
    parts += [(pad, 0.02, 0.3), (breath, 0.08, 0.18)]
    y = mix(*parts)
    y = lowpass(y, SR, 11000)
    return echo(fade_tail(y, SR), SR, 0.08, 0.25, taps=3)


def sfx_save_chime(rng):
    """Gentle bell triad, softly settling over a singing bowl - reassurance that the game is saved."""
    parts = [(bell(nm, 1.2, 0.5, bright=0.7), 0.13 * k, 0.4) for k, nm in enumerate(("G5", "C6", "E6"))]
    parts.append((bell("G6", 1.0, 0.4, bright=0.5), 0.42, 0.25))
    parts.append((struck("bowl", "C4", 1.4, SR, 0.6, rng, mallet=0.0, detune=3.0), 0.0, 0.3))
    y = mix(*parts)
    y = lowpass(y, SR, 8000)
    return echo(fade_tail(y, SR, 0.05), SR, 0.15, 0.3, taps=3)


# --------------------------------------------------------------------------------------------
# Map / menu / UI
# --------------------------------------------------------------------------------------------

def sfx_map_step(rng):
    """Tiny soft footfall on the map path: a muted wood tick and a felt thump (short and quiet)."""
    tick = struck("wood", 520, 0.07, SR, 0.012, rng, mallet=0.25)
    y = mix((tick, 0, 0.6), (thump(200, 120, 0.07, 0.02), 0, 0.6))
    return fade_tail(y, SR, 0.01)


def sfx_map_unlock(rng):
    """Path unlocked: a wooden latch click-clunk, the Glowspore motif (sol-do-re-mi-sol) on kalimba,
    then a ringing bell."""
    click = bandpass(noise(secs(0.02), rng), SR, 2000, 6000) * env_exp(secs(0.02), SR, 0.004, 0.0004)
    clunk = struck("wood", 200, 0.12, SR, 0.04, rng, mallet=0.4)
    parts = [(click, 0, 0.8), (clunk, 0.04, 0.8), (click, 0.08, 0.5)]
    for k, nm in enumerate(("G5", "C6", "D6", "E6", "G6")):
        parts.append((struck("kalimba", nm, 0.5, SR, 0.2, rng, mallet=0.3), 0.2 + 0.07 * k, 0.4))
    parts.append((bell("G6", 0.9, 0.3), 0.55, 0.4))
    parts.append((struck("glass", "D7", 0.7, SR, 0.25, rng, mallet=0.0), 0.57, 0.16))
    y = mix(*parts)
    y = lowpass(y, SR, 9500)
    return echo(fade_tail(y, SR), SR, 0.1, 0.25, taps=3)


def sfx_menu_move(rng):
    """Cursor tick: a tiny wood-block tap with a damped kalimba E6 under it."""
    tick = struck("wood", 1250, 0.045, SR, 0.01, rng, mallet=0.15)
    tine = struck("kalimba", "E6", 0.045, SR, 0.03, rng, mallet=0.0)
    return fade_tail(mix((tick, 0, 0.5), (tine, 0, 0.5)), SR, 0.01)


def sfx_menu_select(rng):
    """Confirm: kalimba E6 and B6 struck together (one strike, not two notes in a row) with a glass shimmer."""
    tick = struck("wood", 1250, 0.04, SR, 0.01, rng, mallet=0.15)
    dyad = struck("kalimba", "E6", 0.3, SR, 0.1, rng, mallet=0.3) + 0.6 * struck("kalimba", "B6", 0.3, SR, 0.08, rng)
    rod = struck("glass", "E7", 0.3, SR, 0.08, rng, mallet=0.0)
    y = mix((tick, 0, 0.35), (dyad, 0, 0.6), (rod, 0.006, 0.15))
    return echo(fade_tail(lowpass(y, SR, 9000), SR), SR, 0.06, 0.15, taps=2)


def sfx_menu_back(rng):
    """Cancel: a lower, felt-damped wood 'tok' with a muted marimba B4."""
    tok = struck("wood", 620, 0.06, SR, 0.015, rng, mallet=0.25)
    bar = struck("marimba", "B4", 0.15, SR, 0.06, rng, mallet=0.15)
    y = mix((tok, 0, 0.5), (bar, 0.004, 0.6))
    return fade_tail(lowpass(y, SR, 5000), SR)


def sfx_dialogue_blip(rng):
    """Soft, short hollow 'pu', meant to repeat per typed character without fatigue."""
    m = secs(0.042)
    f = sweep(760, 700, m)
    y = sine(f, m, SR) + 0.15 * sine(f * 2, m, SR)
    y *= env_adsr(m, SR, 0.003, 0.015, 0.45, 0.015)
    return lowpass(y, SR, 4000)


def sfx_door(rng):
    """Wooden door: a stick-slip creak, then the latch and a soft thunk."""
    creak_len = 0.55
    m = secs(creak_len)
    creak = np.zeros(m)
    pos = 0.0
    k = 0
    while pos < creak_len - 0.01:
        tn = pos / creak_len
        rate = 70 - 35 * tn + 10 * np.sin(2 * np.pi * 3 * tn)
        g = np.sin(np.pi * tn) ** 0.6 * rng.uniform(0.6, 1.0)
        b = secs(0.006)
        burst = (sine(680, b, SR) + 0.6 * sine(1310, b, SR) + 0.3 * sine(2150, b, SR)) * env_exp(b, SR, 0.0018, 0.0002)
        place(creak, g * burst, secs(pos))
        pos += (1.0 / rate) * rng.uniform(0.85, 1.15)
        k += 1
    creak = lowpass(creak, SR, 5000)
    latch = bandpass(noise(secs(0.03), rng), SR, 1500, 5000) * env_exp(secs(0.03), SR, 0.005, 0.0004)
    n = secs(0.2)
    thunk = sine(sweep(125, 60, n), n, SR) * env_exp(n, SR, 0.05, 0.001)
    thunk += 0.5 * lowpass(noise(n, rng), SR, 500) * env_exp(n, SR, 0.03, 0.001)
    y = mix((creak, 0, 0.8), (latch, 0.58, 0.6), (thunk, 0.6, 1.0))
    return echo(fade_tail(y, SR), SR, 0.07, 0.15, taps=2)


def sfx_shop_buy(rng):
    """Chanterelle's till: a wooden drawer 'ka', one warm singing-bowl 'ding' and a jingle of small coins."""
    ka = bandpass(noise(secs(0.035), rng), SR, 900, 3200) * env_exp(secs(0.035), SR, 0.01, 0.0005)
    parts = [(ka, 0, 0.7), (struck("bowl", "G6", 0.8, SR, 0.35, rng, mallet=0.15, detune=6.0), 0.04, 0.55),
             (struck("glass", "D7", 0.5, SR, 0.18, rng, mallet=0.0), 0.045, 0.15)]
    for k in range(6):
        parts.append((sine(rng.uniform(3200, 5200), secs(0.04), SR) * env_exp(secs(0.04), SR, 0.012, 0.0005),
                      0.15 + 0.05 * k + rng.uniform(0, 0.02), 0.18 * (1 - k / 8.0)))
    y = mix(*parts)
    y = lowpass(y, SR, 10000)
    return echo(fade_tail(y, SR), SR, 0.08, 0.25, taps=3)


def sfx_shop_deny(rng):
    """Not enough coins: two dull, felted wood knocks falling a semitone ('tok-tok', marimba E4 -> D#4)."""
    y = mix((struck("wood", 330, 0.1, SR, 0.03, rng, mallet=0.4), 0, 0.6),
            (struck("marimba", "E4", 0.18, SR, 0.08, rng, mallet=0.2), 0, 0.36),
            (struck("wood", 250, 0.16, SR, 0.04, rng, mallet=0.4), 0.13, 0.6),
            (struck("marimba", "D#4", 0.25, SR, 0.1, rng, mallet=0.2), 0.13, 0.36))
    return fade_tail(lowpass(y, SR, 3500), SR)


# --------------------------------------------------------------------------------------------
# Catalogue + output
# --------------------------------------------------------------------------------------------

SOUNDS = [
    # name, builder, volume
    ("Spore Throw", sfx_spore_throw, 0.7),
    ("Spore Hit", sfx_spore_hit, 0.7),
    ("Power Up", sfx_power_up, 1.0),
    ("Power Down", sfx_power_down, 0.8),
    ("Shield Break", sfx_shield_break, 0.85),
    ("Heart Get", sfx_heart_get, 0.8),
    ("Glide Open", sfx_glide_open, 0.6),
    ("Swim Stroke", sfx_swim_stroke, 0.55),
    ("Splash", sfx_splash, 0.75),
    ("Crumble", sfx_crumble, 0.75),
    ("Wind Gust", sfx_wind_gust, 0.6),
    ("Bat Screech", sfx_bat_screech, 0.55),
    ("Boss Hit", sfx_boss_hit, 0.9),
    ("Boss Roar", sfx_boss_roar, 0.45),
    ("Boss Charge", sfx_boss_charge, 0.85),
    ("Boss Defeated", sfx_boss_defeated, 0.75),
    ("Moth Swoop", sfx_moth_swoop, 0.6),
    ("Orb Fire", sfx_orb_fire, 0.7),
    ("Pebble Fall", sfx_pebble_fall, 0.9),
    ("Glowspore Get", sfx_glowspore_get, 0.85),
    ("Level Clear", sfx_level_clear, 0.85),
    ("Map Step", sfx_map_step, 0.6),
    ("Map Unlock", sfx_map_unlock, 0.8),
    ("Menu Move", sfx_menu_move, 0.7),
    ("Menu Select", sfx_menu_select, 0.8),
    ("Menu Back", sfx_menu_back, 0.75),
    ("Dialogue Blip", sfx_dialogue_blip, 0.45),
    ("Door", sfx_door, 0.75),
    ("Shop Buy", sfx_shop_buy, 0.75),
    ("Shop Deny", sfx_shop_deny, 0.65),
    ("Save Chime", sfx_save_chime, 0.7),
    ("Starlit Get", sfx_starlit_get, 0.8),
]


def seed_for(name):
    return int(hashlib.md5(("MushroomMeadow/SFX/" + name).encode("utf-8")).hexdigest()[:8], 16)


def finish(y):
    """Remove DC, trim the inaudible tail, then fade both ends so neither end clicks."""
    y = highpass(np.asarray(y, dtype=float), SR, 20, order=1)
    loud = np.nonzero(np.abs(y) > np.max(np.abs(y)) * 0.003)[0]      # -50 dB below peak
    y = y[: min(len(y), loud[-1] + secs(0.02))].copy()
    first = np.nonzero(np.abs(y) > np.max(np.abs(y)) * 0.001)[0][0]
    y = y[max(0, first - secs(0.001)):]
    fade_tail(y, SR, min(0.03, len(y) / SR * 0.25))
    k = secs(0.0015)
    y[:k] *= np.linspace(0.0, 1.0, k)
    return y


def main():
    os.makedirs(OUT, exist_ok=True)
    only = set(sys.argv[1:])
    for name, fn, volume in SOUNDS:
        if only and name not in only:
            continue
        y = finish(fn(np.random.default_rng(seed_for(name))))
        path = write_wav(name, y, SR, -3.0, False)
        write_resource(name, False, "sfx", volume)
        print("%-15s %6.3f s  %6d bytes" % (name, len(y) / SR, os.path.getsize(path)))


if __name__ == "__main__":
    main()
