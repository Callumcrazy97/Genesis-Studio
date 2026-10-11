"""Mushroom Meadow (full game) sound-effect generator - SNES / chiptune style.

Deterministic: re-running produces byte-identical WAVs. Every sound is 44.1 kHz, 16-bit, mono,
normalised to a -3 dBFS peak, with a faded-in first sample and a faded-out last sample (click free).
Writes into Source/Genesis.Application.Core/Projects/Templates/Assets/TwoDShowcase/Assets/Audio the
.wav, its .audio.json resource document (bus "sfx", loop false) and .audio.json.meta (kind "Audio").

Reuses the phase-1 helpers in audio_gen.py (imported, never modified; phase-1 outputs are not touched).

Usage:  python BuildTools/TemplateContent/MushroomMeadow/sfx_gen.py
Requires numpy + scipy.
"""

import hashlib
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from audio_gen import (OUT, as_array, bandpass, echo, env_adsr, env_exp, fade_tail, highpass,  # noqa: E402
                       lowpass, nhz, noise, place, pulse, render_seq, sine, sweep, tone, triangle,
                       write_resource, write_wav)

SR = 44100


# --------------------------------------------------------------------------------------------
# Small helpers
# --------------------------------------------------------------------------------------------

def secs(d):
    return int(d * SR)      # same rounding as audio_gen.tone(), so envelopes and tones line up


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


def blip(note, dur, duty=0.5, kind="pulse", decay=None, a=0.002, s=0.7, r=0.01, vibrato=0.0):
    n = secs(dur)
    env = env_exp(n, SR, decay, a) if decay else env_adsr(n, SR, a, max(dur * 0.5, 0.01), s, min(r, dur * 0.4))
    env = fade_tail(env.copy(), SR, min(0.004, dur * 0.2))
    return tone(kind, freq_of(note), dur, SR, duty, env, vibrato=vibrato, vib_delay=0.05)


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
    up = 0.5 - 0.5 * np.cos(np.pi * np.clip(t / rise, 0, 1))
    down = 0.5 + 0.5 * np.cos(np.pi * np.clip((t - rise) / (total - rise), 0, 1))
    return np.where(t < rise, up, down)


def bubble(f0, dur, gain=1.0):
    n = secs(dur)
    f = sweep(f0, f0 * 2.3, n)
    e = np.sin(np.pi * np.linspace(0, 1, n)) ** 0.7 * np.exp(-np.linspace(0, 3, n))
    return gain * sine(f, n, SR) * e


def grain(rng, dur, lo, hi):
    n = max(8, secs(dur))
    return bandpass(noise(n, rng), SR, lo, hi) * env_exp(n, SR, dur * 0.35, 0.0008)


# --------------------------------------------------------------------------------------------
# Hero / power-ups
# --------------------------------------------------------------------------------------------

def sfx_spore_throw(rng):
    """Light 'fwip' of a spore leaving Pip's hand: rising squeak + airy swish + tiny pop."""
    n = secs(0.19)
    y = 0.6 * pulse(sweep(380, 1250, n), n, SR, 0.25) * env_adsr(n, SR, 0.002, 0.06, 0.3, 0.06)
    air = svf(noise(n, rng), sweep(1400, 5200, n), q=1.6) * env_exp(n, SR, 0.07, 0.012)
    pop = blip(1900, 0.03, 0.125, decay=0.01)
    y = mix((y, 0), (air, 0, 0.9), (pop, 0.12, 0.35))
    y = lowpass(y, SR, 8500)
    return echo(fade_tail(y, SR), SR, 0.05, 0.18, taps=2)


def sfx_spore_hit(rng):
    """Soft spore 'poff' bursting on something, with a little sparkle of dust."""
    n = secs(0.26)
    puff = lowpass(noise(n, rng, 2), SR, 2400) * env_exp(n, SR, 0.045, 0.001)
    boop = triangle(sweep(720, 210, n), n, SR) * env_exp(n, SR, 0.07)
    sparks = [blip(nm, 0.035, 0.125, decay=0.014) for nm in ("B6", "E7", "G#7")]
    y = mix((puff, 0, 0.9), (boop, 0, 0.8), (sparks[0], 0.04, 0.22), (sparks[1], 0.075, 0.18),
            (sparks[2], 0.11, 0.14))
    return echo(fade_tail(lowpass(y, SR, 9000), SR), SR, 0.06, 0.2, taps=2)


def sfx_power_up(rng):
    """Classic stepped rising arpeggio (C -> D -> E), ending on a ringing fifth."""
    groups = [("C5", "E5", "G5", "C6"), ("D5", "F#5", "A5", "D6"), ("E5", "G#5", "B5", "E6")]
    parts = []
    for gi, grp in enumerate(groups):
        for k, nm in enumerate(grp):
            parts.append(blip(nm, 0.048, 0.5 if k % 2 == 0 else 0.25, a=0.002, s=0.8, r=0.006))
    run = np.concatenate(parts)
    n = secs(0.45)
    tail = 0.6 * tone("pulse", nhz("E6"), 0.45, SR, 0.5, env_exp(n, SR, 0.16), vibrato=0.3, vib_delay=0.04)
    tail += 0.35 * tone("pulse", nhz("B6"), 0.45, SR, 0.25, env_exp(n, SR, 0.12), vibrato=0.3, vib_delay=0.04)
    y = np.concatenate((run, tail))
    y = y + 0.4 * triangle(np.repeat([nhz("C4"), nhz("D4"), nhz("E4"), nhz("E4")],
                                     [secs(0.192), secs(0.192), secs(0.192), len(y) - 3 * secs(0.192)]),
                           len(y), SR) * env_adsr(len(y), SR, 0.003, 0.4, 0.6, 0.15)
    y = lowpass(y, SR, 9000)
    return echo(fade_tail(y, SR), SR, 0.09, 0.3, taps=3)


def sfx_power_down(rng):
    """Deflating, wobbling slide down by thirds: the power slipping away."""
    starts = ["E6", "C6", "A5", "F5", "D5", "B4"]
    parts = []
    for i, nm in enumerate(starts):
        n = secs(0.11)
        f0 = nhz(nm)
        t = np.arange(n) / SR
        f = sweep(f0, f0 * 0.82, n) * (1 + 0.03 * np.sin(2 * np.pi * 28 * t))
        parts.append(pulse(f, n, SR, 0.5 if i % 2 == 0 else 0.25) * env_adsr(n, SR, 0.003, 0.05, 0.6, 0.02))
    y = np.concatenate(parts)
    n = len(y)
    y += 0.45 * triangle(sweep(nhz("E4"), nhz("E3"), n), n, SR) * env_adsr(n, SR, 0.01, 0.3, 0.6, 0.15)
    y *= np.linspace(1.0, 0.55, n)
    y = lowpass(y, SR, 6500)
    return echo(fade_tail(y, SR), SR, 0.08, 0.25, taps=3)


def sfx_shield_break(rng):
    """Bark ring splintering: sharp crack, woody clonks, scattering chips, a falling chip-tone."""
    crack = highpass(noise(secs(0.09), rng), SR, 1300) * env_exp(secs(0.09), SR, 0.016, 0.0005)
    parts = [(crack, 0, 1.0)]
    for f, t0, g in ((430, 0.0, 1.0), (310, 0.035, 0.8), (540, 0.075, 0.6), (265, 0.12, 0.6),
                     (390, 0.19, 0.45), (300, 0.27, 0.32)):
        n = secs(0.12)
        wood = triangle(sweep(f, f * 0.86, n), n, SR) + 0.4 * sine(f * 2.71, n, SR)
        parts.append((wood * env_exp(n, SR, 0.035, 0.0008), t0, 0.55 * g))
    for k in range(14):
        t0 = 0.05 + 0.45 * (k / 14.0) ** 1.3 + rng.uniform(0, 0.02)
        parts.append((grain(rng, rng.uniform(0.01, 0.025), 2000, 6000), t0, 0.35 * (1 - k / 16.0)))
    n = secs(0.14)
    thump = sine(sweep(115, 55, n), n, SR) * env_exp(n, SR, 0.05)
    parts.append((thump, 0, 0.8))
    n = secs(0.3)
    fall = pulse(sweep(1200, 260, n), n, SR, 0.25) * env_exp(n, SR, 0.1)
    parts.append((fall, 0.06, 0.25))
    y = mix(*parts)
    return echo(fade_tail(lowpass(y, SR, 9000), SR), SR, 0.07, 0.22, taps=2)


def sfx_heart_get(rng):
    """Two soft heartbeats, then a sweet rising chime."""
    beats = []
    for t0, g in ((0.0, 1.0), (0.14, 0.75)):
        n = secs(0.12)
        beats.append((sine(sweep(95, 55, n), n, SR) * env_exp(n, SR, 0.04, 0.003), t0, g))
    chime = [(blip(nm, 0.07, 0.25, s=0.8), 0.28 + 0.06 * i, 0.5) for i, nm in enumerate(("E6", "G#6", "B6"))]
    held = bell("E7", 0.7, 0.35)
    pad = triangle(nhz("E5"), secs(0.7), SR) * env_exp(secs(0.7), SR, 0.25)
    y = mix(*(beats + chime + [(held, 0.46, 0.45), (pad, 0.46, 0.35)]))
    y = lowpass(y, SR, 9500)
    return echo(fade_tail(y, SR), SR, 0.1, 0.3, taps=3)


def sfx_glide_open(rng):
    """Dandelion parasol popping open: soft whoomp, fabric flutter, a floaty rising whistle."""
    n = secs(0.6)
    t = np.arange(n) / SR
    whoosh = svf(noise(n, rng), np.concatenate((sweep(500, 3200, secs(0.16)), sweep(3200, 1400, n - secs(0.16)))),
                 q=1.2) * env_adsr(n, SR, 0.03, 0.1, 0.25, 0.2)
    flutter = lowpass(noise(n, rng), SR, 2600) * (0.5 + 0.5 * np.sign(np.sin(2 * np.pi * 24 * t))) \
        * env_exp(n, SR, 0.12, 0.08)
    whistle = tone("tri", sweep(nhz("G5"), nhz("D6"), n), 0.6, SR,
                   env=env_adsr(n, SR, 0.08, 0.2, 0.5, 0.25), vibrato=0.3, vib_rate=6.5, vib_delay=0.15)
    pop = triangle(sweep(240, 140, secs(0.05)), secs(0.05), SR) * env_exp(secs(0.05), SR, 0.015)
    y = mix((whoosh, 0, 0.9), (flutter, 0.02, 0.25), (whistle, 0.04, 0.35), (pop, 0.0, 0.6))
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
    """Rock block giving way: crack, crumbling gravel grains, low rumble."""
    n = secs(0.75)
    rumble = lowpass(noise(n, rng, 3), SR, 260) * env_adsr(n, SR, 0.02, 0.2, 0.5, 0.35)
    crack = highpass(noise(secs(0.06), rng), SR, 900) * env_exp(secs(0.06), SR, 0.012, 0.0005)
    parts = [(rumble, 0, 1.6), (crack, 0, 0.8)]
    for k in range(60):
        t0 = 0.02 + 0.6 * (k / 60.0) ** 1.4 + rng.uniform(0, 0.015)
        lo = rng.uniform(300, 1500)
        parts.append((grain(rng, rng.uniform(0.008, 0.03), lo, lo * 2.5), t0, rng.uniform(0.3, 0.8) * (1 - k / 75.0)))
    for t0, f in ((0.0, 120), (0.22, 95), (0.4, 80)):
        m = secs(0.12)
        parts.append((triangle(sweep(f, f * 0.6, m), m, SR) * env_exp(m, SR, 0.04), t0, 0.5))
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
    """Three squeaky cave-bat chirps with a cavern echo."""
    parts = []
    for i, (t0, dur, f0, fpk, f1) in enumerate(((0.0, 0.07, 3000, 4300, 2700), (0.1, 0.06, 3300, 4600, 3000),
                                                (0.19, 0.12, 2800, 4400, 2200))):
        n = secs(dur)
        t = np.linspace(0, 1, n)
        f = np.where(t < 0.35, f0 + (fpk - f0) * (t / 0.35), fpk + (f1 - fpk) * ((t - 0.35) / 0.65))
        f = f * (1 + 0.04 * np.sin(2 * np.pi * 70 * np.arange(n) / SR))
        y = 0.6 * sine(f, n, SR) + 0.35 * pulse(f, n, SR, 0.125)
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
    """Heavy, satisfying hit on a boss: deep thump, metallic clang, crack, falling chip-tone."""
    n = secs(0.25)
    thump = saturate(sine(sweep(150, 42, n), n, SR) * env_exp(n, SR, 0.09, 0.001), 2.0)
    clang = mix((bell(330, 0.55, 0.22, bright=1.6), 0), (bell(497, 0.45, 0.15, bright=1.4), 0, 0.6))
    crack = highpass(noise(secs(0.06), rng), SR, 1500) * env_exp(secs(0.06), SR, 0.012, 0.0004)
    m = secs(0.22)
    chip = pulse(sweep(900, 180, m), m, SR, 0.5) * env_exp(m, SR, 0.07)
    y = mix((thump, 0, 1.0), (clang, 0, 0.35), (crack, 0, 0.7), (chip, 0.01, 0.35))
    y = lowpass(y, SR, 9000)
    return echo(fade_tail(y, SR), SR, 0.1, 0.25, taps=3)


def sfx_boss_roar(rng):
    """Gritty layered roar with a long pitch drop (Grumblecrag / Duskmantle)."""
    n = secs(1.7)
    t = np.arange(n) / SR
    tn = t / t[-1]
    f = np.where(tn < 0.15, 140 + 40 * (tn / 0.15), 180 * (55 / 180) ** ((tn - 0.15) / 0.85))
    f = f * (1 + 0.07 * np.sin(2 * np.pi * 31 * t) + 0.03 * lowpass(noise(n, rng, 40), SR, 60) * 8)
    growl = 0.55 * pulse(f, n, SR, 0.3) + 0.45 * pulse(f * 1.013, n, SR, 0.45)
    growl *= 0.75 + 0.25 * lowpass(noise(n, rng, 50), SR, 80) * 6
    sub = sine(f * 0.5, n, SR)
    breath = svf(noise(n, rng), 1800 * (1 - 0.6 * tn), q=0.7)
    env = env_adsr(n, SR, 0.07, 0.4, 0.7, 0.5)
    y = (growl + 0.5 * sub + 0.9 * breath) * env
    y = saturate(y, 3.0)
    y = svf(y, 4200 * (1 - 0.75 * tn) + 400, q=0.8, mode="low")
    return echo(fade_tail(y, SR, 0.05), SR, 0.13, 0.28, taps=3, damp=2500)


def sfx_boss_charge(rng):
    """Boss winding up a charge: rising rumble and whine with a quickening tremolo, then a lunge."""
    n = secs(1.05)
    t = np.arange(n) / SR
    tn = t / t[-1]
    rumble = lowpass(noise(n, rng, 2), SR, 240) * (0.3 + 0.7 * tn)
    trem_rate = 6 + 18 * tn
    trem_ph = np.cumsum(trem_rate) / SR
    trem = 0.6 + 0.4 * np.sin(2 * np.pi * trem_ph)
    whine = pulse(sweep(85, 480, n), n, SR, 0.3) * trem * (0.2 + 0.8 * tn ** 1.5)
    whine = lowpass(whine, SR, 3500)
    y = 1.6 * rumble + 0.6 * whine
    y *= np.minimum(1.0, t / 0.05) * np.minimum(1.0, (t[-1] - t) / 0.06 + 0.0)
    m = secs(0.3)
    lunge = svf(noise(m, rng), sweep(3000, 700, m), q=1.0) * env_exp(m, SR, 0.08, 0.004)
    out = mix((y, 0, 1.0), (lunge, 0.98, 0.9))
    return fade_tail(lowpass(out, SR, 8000), SR, 0.02)


def sfx_boss_defeated(rng):
    """Boss collapses: chain of booms and a long falling wail, resolving into a soft light swell."""
    parts = []
    for t0, g, f in ((0.0, 1.0, 110), (0.24, 0.8, 95), (0.5, 0.75, 120), (0.82, 0.65, 85), (1.18, 0.55, 70)):
        m = secs(0.45)
        boom = lowpass(noise(m, rng, 3), SR, 900) * env_exp(m, SR, 0.12, 0.002)
        boom += 1.2 * sine(sweep(f, f * 0.45, m), m, SR) * env_exp(m, SR, 0.14, 0.002)
        parts.append((saturate(boom, 1.6), t0, g))
    m = secs(1.4)
    tt = np.arange(m) / SR
    wail = pulse(sweep(1100, 90, m) * (1 + 0.04 * np.sin(2 * np.pi * 9 * tt)), m, SR, 0.5)
    parts.append((wail * env_adsr(m, SR, 0.01, 0.6, 0.5, 0.3), 0.05, 0.3))
    m = secs(1.5)
    glow = np.zeros(m)
    for nm, g in (("C6", 0.5), ("E6", 0.4), ("G6", 0.4), ("C7", 0.25)):
        glow += g * sine(nhz(nm), m, SR) * (0.8 + 0.2 * np.sin(2 * np.pi * 7 * np.arange(m) / SR))
    glow *= raised(m, 0.35, 1.0)
    parts.append((glow, 1.35, 0.35))
    for k, nm in enumerate(("G6", "C7", "E7", "G7")):
        parts.append((bell(nm, 0.5, 0.18), 1.55 + 0.09 * k, 0.18))
    y = mix(*parts)
    y = lowpass(y, SR, 8500)
    return echo(fade_tail(y, SR, 0.05), SR, 0.15, 0.3, taps=4)


# --------------------------------------------------------------------------------------------
# Rewards / jingles
# --------------------------------------------------------------------------------------------

def sfx_glowspore_get(rng):
    """Shimmering magical fanfare (~3 s): harp sweep, a proud D-major call, twinkling held chord."""
    bpm = 132
    beat = 60.0 / bpm
    parts = []
    harp = ["D5", "F#5", "A5", "D6", "E6", "F#6", "A6", "D7"]
    for k, nm in enumerate(harp):
        parts.append((bell(nm, 0.6, 0.25), 0.045 * k, 0.21))
        parts.append((blip(nm, 0.06, 0.25, decay=0.03), 0.045 * k, 0.15))
    t0 = 0.36
    hold = 2.0
    lead = [("A5", .5), ("D6", .5), ("F#6", 1.0), ("E6", .5), ("F#6", .5), ("A6", hold)]
    harm = [("F#5", .5), ("A5", .5), ("D6", 1.0), ("C#6", .5), ("D6", .5), ("F#6", hold)]
    bass = [("D3", 1.0), ("A2", 1.0), ("G2", .5), ("A2", .5), ("D3", hold)]
    parts.append((render_seq(lead, SR, bpm, "pulse", 0.5, 0.55, vibrato=0.25, legato=0.95), t0, 1.0))
    parts.append((render_seq(harm, SR, bpm, "pulse", 0.25, 0.3, vibrato=0.25, legato=0.95), t0, 1.0))
    parts.append((render_seq(bass, SR, bpm, "tri", gain=0.8, legato=0.97), t0, 1.0))
    # Held shimmer pad under the final note, chorused sines with a slow tremolo.
    hold_start = t0 + 3.0 * beat
    m = secs(hold * beat + 0.1)
    tt = np.arange(m) / SR
    pad = np.zeros(m)
    for nm, g in (("D6", 0.5), ("F#6", 0.4), ("A6", 0.4), ("D7", 0.25)):
        f = nhz(nm)
        pad += g * (sine(f, m, SR) + sine(f * 1.004, m, SR)) * 0.5
    pad *= (0.75 + 0.25 * np.sin(2 * np.pi * 6 * tt)) * raised(m, 0.25, 1.0)
    parts.append((pad, hold_start, 0.25))
    # Twinkles: pentatonic bell sparkles across the held chord, fading out.
    penta = ["D7", "E7", "F#7", "A7", "B7", "A6", "F#7", "D7"]
    for k in range(13):
        nm = penta[int(rng.integers(0, len(penta)))]
        parts.append((bell(nm, 0.3, 0.08, bright=0.6), hold_start + 0.07 * k + rng.uniform(0, 0.03),
                      0.16 * (1 - k / 16.0)))
    y = mix(*parts)
    y = lowpass(y, SR, 10000)
    return echo(fade_tail(y, SR, 0.05), SR, 0.13, 0.3, taps=3)


def sfx_level_clear(rng):
    """Bouncy F-major victory jingle (~3.6 s) with kick/snare, a roll and a crash on the last note."""
    bpm = 150
    beat = 60.0 / bpm
    lead = [("C5", .5), ("F5", .5), ("A5", .5), ("C6", .5), ("Bb5", .33), ("A5", .33), ("G5", .34), ("A5", 1.0),
            ("G5", .5), ("A5", .5), ("Bb5", .5), ("D6", .5), ("C6", .33), ("D6", .33), ("E6", .34), ("F6", 1.5)]
    harm = [(None, 2.0), ("E5", 1.0), ("F5", 1.0), ("D5", .5), ("F5", .5), ("G5", .5), ("Bb5", .5),
            ("G5", 1.0), ("A5", 1.5)]
    bass = [("F2", 1), ("C3", 1), ("F2", 1), ("A2", 1), ("Bb2", 1), ("G2", 1), ("C3", 1), ("F2", 1.5)]
    a = render_seq(lead, SR, bpm, "pulse", 0.5, 0.55, vibrato=0.2, legato=0.88)
    b = render_seq(harm, SR, bpm, "pulse", 0.25, 0.3, vibrato=0.2, legato=0.85)
    c = render_seq(bass, SR, bpm, "tri", gain=0.85, legato=0.8)
    parts = [(a, 0), (b, 0), (c, 0)]
    m = secs(0.15)
    kick = sine(sweep(150, 50, m), m, SR) * env_exp(m, SR, 0.06)
    m = secs(0.12)
    snare = highpass(noise(m, rng, 2), SR, 900) * env_exp(m, SR, 0.04) * 0.8
    for k in range(7):
        parts.append((kick, k * beat, 0.5))
        if k % 2 == 1:
            parts.append((snare, k * beat, 0.4))
    for k in range(4):
        parts.append((snare, (6.0 + k * 0.25) * beat, 0.2 + 0.06 * k))
    m = secs(1.2)
    crash = highpass(noise(m, rng), SR, 4500) * env_exp(m, SR, 0.35, 0.002)
    parts.append((crash, 7.0 * beat, 0.22))
    parts.append((kick, 7.0 * beat, 0.6))
    y = mix(*parts)
    y = lowpass(y, SR, 9500)
    return echo(fade_tail(y, SR, 0.05), SR, 0.12, 0.3, taps=4)


def sfx_starlit_get(rng):
    """Starlit Firefly: a dazzling upward glissando that bursts into a twinkling trill."""
    penta = ["C6", "D6", "E6", "G6", "A6", "C7", "D7", "E7", "G7", "A7", "C8"]
    gl = np.concatenate([blip(nm, 0.03, 0.125, a=0.001, s=0.9, r=0.004) for nm in penta])
    n = secs(0.9)
    t = np.arange(n) / SR
    trill_f = np.where((t * 30).astype(int) % 2 == 0, nhz("E7"), nhz("G7"))
    trill = pulse(trill_f, n, SR, 0.25) * env_exp(n, SR, 0.3)
    sparkle = highpass(noise(n, rng), SR, 7000) * (0.5 + 0.5 * np.sin(2 * np.pi * 13 * t)) * env_exp(n, SR, 0.25, 0.02)
    y = mix((gl, 0, 0.5), (trill, len(gl) / SR, 0.3), (bell("C7", 0.9, 0.3), len(gl) / SR, 0.5),
            (sparkle, len(gl) / SR, 0.12),
            (triangle(nhz("C5"), secs(1.2), SR) * env_exp(secs(1.2), SR, 0.35), 0.05, 0.3))
    y = lowpass(y, SR, 11000)
    return echo(fade_tail(y, SR), SR, 0.08, 0.32, taps=4)


def sfx_save_chime(rng):
    """Gentle bell triad, softly settling - reassurance that the game is saved."""
    parts = [(bell(nm, 1.2, 0.5, bright=0.7), 0.13 * k, 0.4) for k, nm in enumerate(("G5", "C6", "E6"))]
    parts.append((bell("G6", 1.0, 0.4, bright=0.5), 0.42, 0.25))
    m = secs(1.4)
    parts.append((triangle(nhz("C4"), m, SR) * env_adsr(m, SR, 0.08, 0.4, 0.5, 0.5), 0.0, 0.3))
    y = mix(*parts)
    y = lowpass(y, SR, 8000)
    return echo(fade_tail(y, SR, 0.05), SR, 0.15, 0.3, taps=3)


# --------------------------------------------------------------------------------------------
# Map / menu / UI
# --------------------------------------------------------------------------------------------

def sfx_map_step(rng):
    """Tiny soft footstep pluck for the map marker (plays every hop: kept short and quiet)."""
    m = secs(0.085)
    y = triangle(sweep(560, 380, m), m, SR) * env_exp(m, SR, 0.022, 0.001)
    y += 0.35 * lowpass(noise(m, rng), SR, 2200) * env_exp(m, SR, 0.008, 0.0005)
    return fade_tail(y, SR, 0.01)


def sfx_map_unlock(rng):
    """Path unlocked: a lock click-clunk, then a rising sparkle and a ringing bell."""
    click = bandpass(noise(secs(0.02), rng), SR, 2000, 6000) * env_exp(secs(0.02), SR, 0.004, 0.0004)
    m = secs(0.12)
    clunk = triangle(sweep(220, 130, m), m, SR) * env_exp(m, SR, 0.04)
    parts = [(click, 0, 0.8), (clunk, 0.04, 0.8), (click, 0.08, 0.5)]
    for k, nm in enumerate(("G5", "B5", "D6", "G6", "B6")):
        parts.append((blip(nm, 0.06, 0.25, s=0.8), 0.2 + 0.055 * k, 0.4))
    parts.append((bell("G6", 0.9, 0.3), 0.475, 0.5))
    parts.append((bell("D7", 0.7, 0.2, bright=0.6), 0.5, 0.2))
    y = mix(*parts)
    y = lowpass(y, SR, 9500)
    return echo(fade_tail(y, SR), SR, 0.1, 0.3, taps=3)


def sfx_menu_move(rng):
    """Cursor tick: one short, bright pip (B5). The menu set shares timbre and key (E major)."""
    return blip("B5", 0.045, 0.25, a=0.001, s=0.6, r=0.012)


def sfx_menu_select(rng):
    """Confirm: the move pip answered by a fourth up (B5 -> E6), a touch of echo."""
    a = blip("B5", 0.045, 0.25, a=0.001, s=0.7, r=0.008)
    b = blip("E6", 0.16, 0.5, decay=0.06, a=0.001)
    b += 0.4 * blip("E5", 0.16, 0.25, kind="tri", decay=0.07)
    y = np.concatenate((a, b))
    return echo(fade_tail(lowpass(y, SR, 9000), SR), SR, 0.06, 0.2, taps=2)


def sfx_menu_back(rng):
    """Cancel: the same pip falling a fourth (B5 -> F#5), thinner and duller."""
    a = blip("B5", 0.04, 0.125, a=0.001, s=0.6, r=0.008)
    b = blip("F#5", 0.12, 0.125, decay=0.04, a=0.001)
    y = np.concatenate((a, b))
    return fade_tail(lowpass(y, SR, 5000), SR)


def sfx_dialogue_blip(rng):
    """Soft, short voice pip, meant to repeat per typed character without fatigue."""
    m = secs(0.042)
    f = sweep(760, 700, m)
    y = sine(f, m, SR) + 0.18 * triangle(f, m, SR)
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
    """Chanterelle's till: a 'ka' then a bright double 'ching', with a coin jingle."""
    ka = bandpass(noise(secs(0.035), rng), SR, 900, 3200) * env_exp(secs(0.035), SR, 0.01, 0.0005)
    parts = [(ka, 0, 0.7), (bell("B6", 0.6, 0.25, bright=1.5), 0.05, 0.5),
             (bell("E7", 0.7, 0.3, bright=1.5), 0.12, 0.5),
             (blip("E7", 0.09, 0.25, decay=0.05), 0.12, 0.15)]
    for k in range(6):
        parts.append((sine(rng.uniform(3200, 5200), secs(0.04), SR) * env_exp(secs(0.04), SR, 0.012, 0.0005),
                      0.15 + 0.05 * k + rng.uniform(0, 0.02), 0.18 * (1 - k / 8.0)))
    y = mix(*parts)
    y = lowpass(y, SR, 10000)
    return echo(fade_tail(y, SR), SR, 0.08, 0.25, taps=3)


def sfx_shop_deny(rng):
    """Not enough coins: a low, buzzy 'uh-uh' (two falling notes)."""
    def buzz(nm, dur):
        n = secs(dur)
        f = nhz(nm)
        y = pulse(f, n, SR, 0.5) + 0.7 * pulse(f * 1.012, n, SR, 0.3)
        return y * env_adsr(n, SR, 0.004, 0.05, 0.75, 0.02)
    y = mix((buzz("D#4", 0.1), 0, 0.5), (buzz("A#3", 0.19), 0.13, 0.5))
    return fade_tail(lowpass(y, SR, 2200), SR)


# --------------------------------------------------------------------------------------------
# Catalogue + output
# --------------------------------------------------------------------------------------------

SOUNDS = [
    # name, builder, volume
    ("Spore Throw", sfx_spore_throw, 0.7),
    ("Spore Hit", sfx_spore_hit, 0.7),
    ("Power Up", sfx_power_up, 0.8),
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
    ("Boss Roar", sfx_boss_roar, 0.9),
    ("Boss Charge", sfx_boss_charge, 0.85),
    ("Boss Defeated", sfx_boss_defeated, 0.9),
    ("Moth Swoop", sfx_moth_swoop, 0.6),
    ("Orb Fire", sfx_orb_fire, 0.7),
    ("Pebble Fall", sfx_pebble_fall, 0.6),
    ("Glowspore Get", sfx_glowspore_get, 0.85),
    ("Level Clear", sfx_level_clear, 0.85),
    ("Map Step", sfx_map_step, 0.45),
    ("Map Unlock", sfx_map_unlock, 0.8),
    ("Menu Move", sfx_menu_move, 0.5),
    ("Menu Select", sfx_menu_select, 0.6),
    ("Menu Back", sfx_menu_back, 0.55),
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
    for name, fn, volume in SOUNDS:
        y = finish(fn(np.random.default_rng(seed_for(name))))
        path = write_wav(name, y, SR, -3.0, False)
        write_resource(name, False, "sfx", volume)
        print("%-15s %6.3f s  %6d bytes" % (name, len(y) / SR, os.path.getsize(path)))


if __name__ == "__main__":
    main()
