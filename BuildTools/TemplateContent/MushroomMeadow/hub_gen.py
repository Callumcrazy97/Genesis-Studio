"""Mushroom Meadow HUB content generator: the Overworld map room and the Hearthollow village.

Re-runnable and deterministic. Run from anywhere:
    python BuildTools/TemplateContent/MushroomMeadow/hub_gen.py
Writes into Source/Genesis.Application.Core/Projects/Templates/Assets/TwoDShowcase/Assets:

  Rooms           Overworld, Hearthollow
  Objects         Map Marker (Pip's marker + the Overworld controller), Map Node, Map Clouds,
                  Village Host (dialogue box, Chanterelle's shop, leaving the village), Lantern Tree Glow,
                  NPC Elder Morel, NPC Chanterelle, NPC Bram, NPC Puffball, NPC Old Wick, Shop Stall, Exit Sign,
                  Chimney Smoke (live smoke puffs over the Village Houses chimneys)
  Scripts         Hub Common (shared helpers), Village Lines (every NPC line, by story progress, and the
                  reputation-aware lines: HubRepPages)
  User Interfaces Overworld HUD, Dialogue Box, Shop Menu
  Particles       Map Unlock Burst, Path Sparkle, Node Glow, Village Fireflies, Shop Sparkle
  Images          Smoke Puff (the one piece of art made here; needs Pillow)

Art (Pip Map, Map Node, NPC sprites, portraits, Overworld Map, Village tiles/backgrounds, Lantern
Tree Lights 1..5) and audio come from the ART-*/AUDIO-* generators; this file only names them.
The chimney positions are read from worlds_art.py (Village.chimneys()), which paints the chimneys
without smoke.

Dialogue text lives in LINES below. A page is wrapped here (56 characters a line, at most 3 lines;
longer pages continue on a new page). In the generated PGSL, pages are joined by "~", lines by "/",
and a page starting with "@" is spoken by Pip.
"""
import hashlib
import json
import os
import textwrap

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "..", ".."))
ASSETS = os.path.join(ROOT, "Source", "Genesis.Application.Core", "Projects", "Templates",
                      "Assets", "TwoDShowcase", "Assets")
STAMP = "2026-10-11T00:00:00Z"
WRAP = 56


def guid(name):
    return hashlib.md5(("mushroom-meadow-hub:" + name).encode("utf-8")).hexdigest()


def write_text(rel, text):
    path = os.path.join(ASSETS, rel)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    text.encode("ascii")  # keep every generated file plain ASCII
    with open(path, "w", encoding="utf-8", newline="\n") as handle:
        handle.write(text)


def write_json(rel, data):
    write_text(rel, json.dumps(data, indent=2) + "\n")


def write_meta(rel, kind, display):
    path = os.path.join(ASSETS, rel + ".meta")
    existing = None
    if os.path.exists(path):
        try:
            with open(path, encoding="utf-8") as handle:
                existing = json.load(handle).get("guid")
        except (OSError, ValueError):
            existing = None
    write_json(rel + ".meta", {"guid": existing or guid(rel), "kind": kind, "displayName": display,
                               "createdUtc": STAMP, "modifiedUtc": STAMP})


def colour(r, g, b, a=1.0):
    return {"r": r, "g": g, "b": b, "a": a}


# ====================================================================== particles
PARTICLE_BASE = {
    "preview2D": True, "maxParticles": 64, "emitRate": 0.0, "burstCount": 12, "loop": False,
    "duration": 1.0, "shape": "Sphere", "spreadDegrees": 30.0, "emitRadius": 0.1, "speed": 6.0,
    "speedVariance": 0.35, "gravity": 0.0, "drag": 1.0, "lifetime": 0.5, "lifetimeVariance": 0.25,
    "startSize": 0.3, "endSize": 0.08, "emissive": 1.0, "blendMode": "Alpha", "pixelSampling": True,
    "startColor": colour(1, 1, 1, 1), "endColor": colour(1, 1, 1, 0),
}


def particle(name, notes, **overrides):
    data = dict(PARTICLE_BASE)
    data.update(overrides)
    data["effectName"] = name
    data["notes"] = notes
    rel = "Particles/" + name + ".particle.json"
    write_json(rel, data)
    write_meta(rel, "Particle", name)


def build_particles():
    # 2D rooms draw 12 px per particle unit; gravity is positive upwards.
    particle("Map Unlock Burst", "Overworld: a Glowspore's light bursting at a map node (unlock reveal).",
             burstCount=40, maxParticles=80, shape="Sphere", speed=7.5, speedVariance=0.4, drag=2.2,
             gravity=0.8, lifetime=0.95, startSize=0.36, endSize=0.06, blendMode="Additive", emissive=1.6,
             startColor=colour(1.0, 0.98, 0.82, 1), midColor=colour(1.0, 0.84, 0.36, 1),
             endColor=colour(0.95, 0.55, 0.2, 0), colorJitter=0.15)
    particle("Path Sparkle", "Overworld: sparkles that run along a path as it is revealed.",
             burstCount=7, maxParticles=16, shape="Sphere", speed=1.6, drag=2.0, gravity=1.2,
             lifetime=0.7, startSize=0.26, endSize=0.04, blendMode="Additive", emissive=1.4,
             startColor=colour(1.0, 0.96, 0.7, 1), endColor=colour(1.0, 0.7, 0.25, 0))
    particle("Node Glow", "Overworld, looping: warm motes rising from a cleared node's Glowspore.",
             loop=True, burstCount=0, emitRate=3.5, maxParticles=16, shape="Disc", emitRadius=0.5,
             spreadDegrees=20, speed=0.9, speedVariance=0.4, drag=0.4, gravity=0.6,
             lifetime=1.6, lifetimeVariance=0.3, startSize=0.2, endSize=0.05, blendMode="Additive",
             emissive=1.5, startColor=colour(1.0, 0.95, 0.66, 0.95), endColor=colour(1.0, 0.75, 0.3, 0))
    particle("Village Fireflies", "Hearthollow, looping: fireflies drifting over the whole village "
             "(Village Host spawns one emitter at the room's centre).",
             loop=True, burstCount=0, emitRate=5.0, maxParticles=60, shape="Box", boxSizeX=220.0,
             boxSizeY=11.0, boxSizeZ=0.0, speed=0.35, speedVariance=0.8, drag=0.5, gravity=0.08,
             turbulenceStrength=1.1, lifetime=6.5, lifetimeVariance=0.3, startSize=0.22, endSize=0.16,
             blendMode="Additive", emissive=1.6, startColor=colour(0.85, 1.0, 0.45, 0.0),
             midColor=colour(1.0, 0.95, 0.5, 0.95), endColor=colour(0.9, 0.85, 0.35, 0.0))
    particle("Shop Sparkle", "Hearthollow: coins and stars when Chanterelle hands over a purchase.",
             burstCount=18, maxParticles=36, shape="Cone", spreadDegrees=50, speed=7.0, drag=1.8,
             gravity=-4.0, lifetime=0.8, startSize=0.34, endSize=0.1, blendMode="Additive", emissive=1.4,
             startColor=colour(1.0, 0.95, 0.55, 1), midColor=colour(1.0, 0.82, 0.25, 1),
             endColor=colour(0.91, 0.6, 0.11, 0))


# ====================================================================== user interfaces
def element(eid, etype, x, y, w, h, order, **extra):
    data = {"id": eid, "parentId": "", "type": etype, "anchor": "TopLeft", "x": float(x), "y": float(y),
            "width": float(w), "height": float(h), "order": order, "visible": True, "text": "",
            "font": "Consolas", "fontSize": 18.0, "image": "", "background": "#00000000",
            "foreground": "#FFFFFFFF", "accent": "#FFFFD23F"}
    data.update(extra)
    return data


def panel(eid, x, y, w, h, order, top="#F2223548", bottom="#F2141F2E", radius=12.0, border=3.0,
          border_colour="#FFFFD23F"):
    return element(eid, "Panel", x, y, w, h, order, background=top, fill="VerticalGradient",
                   gradientEnd=bottom, cornerRadius=radius, borderWidth=border, borderColor=border_colour)


def text(eid, x, y, w, h, order, value, size, colour_hex="#FFF8FCF4", align="Left", valign="Middle", spacing=0.0):
    return element(eid, "Text", x, y, w, h, order, text=value, fontSize=size, foreground=colour_hex,
                   textAlign=align, textVerticalAlign=valign, letterSpacing=spacing)


def image(eid, x, y, w, h, order, asset):
    return element(eid, "Image", x, y, w, h, order, image="Assets/Sprites/%s.image.json" % asset,
                   imageFit="Contain", borderWidth=0.0)


def write_ui(name, elements):
    rel = "User Interfaces/%s.ui.json" % name
    ids = [e["id"] for e in elements]
    assert len(ids) == len(set(ids)), name
    write_json(rel, {"schemaVersion": 1, "designWidth": 1280, "designHeight": 720, "elements": elements})
    write_meta(rel, "UserInterface", name)


# Chanterelle's stall: 11 rows in two columns (indices 0-5 left, 6-10 right); the last row is "Leave".
SHOP_ROW_W = 480
SHOP_COLUMNS = (166, 654)


def shop_row_pos(i):
    """Top-left of shop row i in the 1280x720 design."""
    col = 0 if i < 6 else 1
    row = i if i < 6 else i - 6
    return SHOP_COLUMNS[col], 134 + row * 44


PORTRAITS = [("portraitPip", "Portrait Pip"), ("portraitMorel", "Portrait Elder Morel"),
             ("portraitChanterelle", "Portrait Chanterelle"), ("portraitBram", "Portrait Bram"),
             ("portraitPuffball", "Portrait Puffball"), ("portraitWick", "Portrait Old Wick"),
             ("portraitPipSpore1", "Portrait Pip Spore 1"), ("portraitPipSpore2", "Portrait Pip Spore 2"),
             ("portraitPipSpore3", "Portrait Pip Spore 3")]


def build_ui():
    # ---- Overworld HUD: node banner, best medals, Glowspore count, purse, help, menu, toast.
    o = [
        panel("bannerPanel", 330, 16, 620, 124, 0),
        element("bannerSheen", "Panel", 338, 20, 604, 6, 1, background="#30FFFFFF", cornerRadius=3.0,
                borderWidth=0.0),
        text("bannerTitle", 330, 22, 620, 44, 2, "HEARTHOLLOW", 30.0, "#FFFFEBB9", "Center", spacing=2.0),
        text("bannerSub", 330, 66, 620, 30, 3, "", 18.0, "#FFDCEFE0", "Center"),
        element("statusPlate", "Panel", 344, 30, 112, 28, 4, background="#FF4CAF3C", fill="VerticalGradient",
                gradientEnd="#FF2F7D32", cornerRadius=6.0, borderWidth=2.0, borderColor="#FF2A1D2E"),
        text("statusText", 344, 30, 112, 28, 5, "VILLAGE", 18.0, "#FFFFF4D6", "Center", spacing=1.0),
        text("medalLabel", 470, 102, 112, 26, 6, "MEDALS", 18.0, "#FFFFE7A0", "Center", spacing=1.0),
    ]
    for i in range(3):
        o.append(element("medalPip%d" % (i + 1), "Panel", 596 + i * 30, 104, 22, 22, 7 + i,
                         background="#FF2A1D2E", cornerRadius=11.0, borderWidth=2.0, borderColor="#FFE89A1B"))
        o.append(element("medalFill%d" % (i + 1), "Panel", 600 + i * 30, 108, 14, 14, 10 + i,
                         background="#FFFFD23F", fill="VerticalGradient", gradientEnd="#FFE89A1B",
                         cornerRadius=7.0, borderWidth=0.0))
    o += [
        panel("glowPanel", 1040, 16, 220, 64, 20),
        image("glowIcon", 1054, 28, 40, 40, 21, "Icon Glowspore"),
        text("glowText", 1100, 16, 150, 64, 22, "0 / 5", 28.0, "#FFFFF1C8", "Left", spacing=1.0),
        panel("pursePanel", 20, 16, 250, 64, 23),
        image("purseCoin", 34, 30, 22, 22, 24, "Coin Icon"),
        text("coinsText", 62, 16, 90, 64, 25, "0", 22.0, "#FFF8FCF4"),
        image("purseHeart", 150, 31, 20, 20, 26, "Heart Icon"),
        text("livesText", 176, 16, 90, 64, 27, "x5", 22.0, "#FFF8FCF4"),
        element("helpBar", "Panel", 20, 668, 1240, 38, 30, background="#D61D3043", cornerRadius=8.0,
                borderWidth=2.0, borderColor="#80FFE7A0"),
        text("helpText", 36, 668, 1210, 38, 31,
             "ARROWS / D-PAD: WALK    ENTER / A: PLAY    ESC / START: MENU", 18.0, "#FFF0F1DB"),
        element("toastPanel", "Panel", 1040, 88, 220, 36, 32, background="#E0203550", cornerRadius=8.0,
                borderWidth=2.0, borderColor="#FFFFD23F"),
        text("toastText", 1040, 88, 220, 36, 33, "PROGRESS SAVED", 18.0, "#FFFFE7A0", "Center"),
        element("menuShade", "Panel", 0, 0, 1280, 720, 40, background="#801A1020", borderWidth=0.0),
        panel("menuPanel", 420, 206, 440, 340, 41, radius=14.0, border=4.0),
        text("menuTitle", 420, 222, 440, 48, 42, "THE VALLEY MAP", 30.0, "#FFFFDF91", "Center", spacing=2.0),
        text("menuOpt0", 420, 288, 440, 44, 43, "RESUME", 24.0, "#FFDCEFE0", "Center"),
        text("menuOpt1", 420, 342, 440, 44, 44, "COLLECTION", 24.0, "#FFDCEFE0", "Center"),
        text("menuOpt2", 420, 396, 440, 44, 46, "HOW TO PLAY", 24.0, "#FFDCEFE0", "Center"),
        text("menuOpt3", 420, 450, 440, 44, 47, "SAVE & QUIT TO TITLE", 24.0, "#FFDCEFE0", "Center"),
        text("menuHint", 420, 506, 440, 30, 45, "ENTER: CHOOSE   ESC: BACK", 18.0, "#B0FFF1C8", "Center"),
    ]
    write_ui("Overworld HUD", o)

    # ---- Dialogue Box: portrait frame + one portrait image per speaker, name plate, text area.
    d = [
        panel("dlgPanel", 96, 436, 1088, 220, 0, radius=14.0, border=4.0),
        element("dlgInner", "Panel", 106, 446, 1068, 200, 1, background="#00000000", cornerRadius=10.0,
                borderWidth=2.0, borderColor="#80FFF1C8"),
        element("portraitFrame", "Panel", 118, 458, 176, 176, 2, background="#FF3A2A3F", fill="VerticalGradient",
                gradientEnd="#FF241A2C", cornerRadius=10.0, borderWidth=3.0, borderColor="#FFFFE7A0"),
    ]
    for i, (eid, asset) in enumerate(PORTRAITS):
        d.append(image(eid, 126, 466, 160, 160, 3 + i, asset))
    d += [
        element("namePlate", "Panel", 312, 414, 270, 42, 20, background="#FFD8433B", fill="VerticalGradient",
                gradientEnd="#FFA52D2B", cornerRadius=8.0, borderWidth=2.0, borderColor="#FF2A1D2E"),
        text("nameText", 312, 414, 270, 42, 21, "", 22.0, "#FFFFF4D6", "Center", spacing=1.0),
    ]
    write_ui("Dialogue Box", d)

    # ---- Shop Menu: Chanterelle's stall, two columns of rows (see SHOP_COLUMNS). Names, prices and the
    # highlight are drawn by Village Host.
    s = [
        panel("shopPanel", 150, 104, 980, 300, 0, radius=14.0, border=4.0),
        element("shopRibbon", "Panel", 440, 86, 400, 38, 1, background="#FFD8433B", fill="VerticalGradient",
                gradientEnd="#FFA52D2B", cornerRadius=6.0, borderWidth=2.0, borderColor="#FF2A1D2E"),
        text("shopTitle", 440, 86, 400, 38, 2, "CHANTERELLE'S STALL", 19.0, "#FFFFF4D6", "Center", spacing=1.5),
    ]
    icons = ["Heart Berry Sprite", "Explorer Idle", "Glide Seed Sprite", "Spore Puff Sprite", "Bark Shield Sprite",
             "Item Sword Sprite", "Item Shield Sprite", "Item Bow Sprite", "Arrow Sprite",
             "Item Propeller Flower Sprite", "Exit Sign Sprite"]
    for i, asset in enumerate(icons):
        rx, ry = shop_row_pos(i)
        s.append(element("row%d" % i, "Panel", rx, ry, SHOP_ROW_W, 40, 3 + i, background="#40FFF1C8",
                         cornerRadius=6.0, borderWidth=0.0))
        s.append(image("icon%d" % i, rx + 10, ry + 4, 32, 32, 20 + i, asset))
        if i < len(icons) - 1:
            s.append(image("coin%d" % i, rx + SHOP_ROW_W - 34, ry + 12, 16, 16, 40 + i, "Coin Icon"))
    s += [
        # The purse sits in the free cell under the right column (it used to sit at the top right, behind the HUD bar).
        panel("pursePanel", 934, 354, 192, 40, 60),
        image("purseCoin", 946, 362, 24, 24, 61, "Coin Icon"),
        text("purseText", 978, 354, 142, 40, 62, "0", 22.0, "#FFFFF1C8"),
    ]
    write_ui("Shop Menu", s)


# ====================================================================== dialogue
# stage: 0..5 = Glowspores returned, 6 = after the ending (gameComplete). Each entry is
# (first-visit pages, repeat-visit pages). Missing stages fall back to the previous stage.
LINES = {
    1: {  # Elder Morel: quest giver, story updates per progress
        0: (["Ah, Pip. Come closer, little wanderer. My eyes are older than the moss on this old stick.",
             "Last night Duskmantle, the dusk-moth queen, tore the five Glowspores from the Great Lantern Tree.",
             "Without them the long night creeps in. The mushrooms nap at noon, and the bees forget their way home.",
             "@I'll bring them back, Elder. All five of them. Where do I start?",
             "The first one fell into Mushroom Meadow, just east along the map path. Take my old lantern, and mind the acorns."],
            ["Mushroom Meadow is east along the map path. The first Glowspore is waiting at the summit.",
             "And if the acorns give you trouble, hop on their heads. It worked for me. Sixty summers ago, mind you."]),
        1: (["One Glowspore home! Did you see, Pip? The lowest branch of the Tree is glowing again.",
             "The second fell deep under the hills, into Rootdeep Caverns. Something big and grumbly lives down there.",
             "@Big and grumbly. Wonderful.",
             "Keep your lantern high. Crystals light the way for those who look for them."],
            ["Rootdeep Caverns lies past the meadow. Big, grumbly, and very much in the dark. Be brave, little one."]),
        2: (["Two lights in the Tree! Old Wick has been humming all morning. Off-key, but humming.",
             "The third Glowspore sank into Tidepool Grotto, beyond the cliffs. You will have to swim, little one.",
             "@Swim? With this hat?",
             "Hats float, Pip. Mostly. And mind the jellies: even I never found a way to stomp a jelly."],
            ["Tidepool Grotto is down by the coast. Press jump to swim upward, and keep well away from the jellies."]),
        3: (["Three! The middle boughs are warm again. I can read my letters without squinting.",
             "The fourth Glowspore was carried up to Windswept Canopy, where the giant trees touch the clouds.",
             "The winds there push and pull. A Glide Seed from Chanterelle might serve you well."],
            ["Windswept Canopy, high in the treetops. Watch the leaves: when they swirl, the wind is coming."]),
        4: (["Four Glowspores home. Only one remains, and Duskmantle keeps it for herself.",
             "She waits in Duskmantle Hollow, in her nest tower at the very end of the valley.",
             "@Is she... very big?",
             "Very. But she is afraid of the light, Pip. And you have carried light all this way."],
            ["Duskmantle Hollow lies at the end of the valley. Stomp her when she lands, dazed. I believe in you."]),
        5: (["All five Glowspores! The Great Lantern Tree blazes once more. Hearthollow will never forget this, Pip."],
            ["All five, home at last. Listen: even the crickets are singing in tune."]),
        6: (["Look at the Tree, Pip. Every branch alight, just like when I was a sprout.",
             "The bees found their way home. The mushrooms dance at dusk again. And Duskmantle? I hear she took up stargazing.",
             "@Maybe I'll visit the Meadow again. Just for fun this time.",
             "That is what the light is for, little wanderer. Go on. The whole valley is yours."],
            ["Every branch alight. I could watch it all night. Which is lucky, because there is no long night left to fear."]),
    },
    2: {  # Chanterelle: shopkeeper (the shop opens after the last page)
        0: (["Welcome, welcome! Chanterelle's Trading Stall: the finest goods this side of the meadow!",
             "Heart Berries, Glide Seeds, Spore Puffs, Bark Shields... have a look. Coins at the ready!"],
            ["Back for more? Chanterelle always has more."]),
        1: (["Pip! I heard the Tree's first light came back. Business is blooming already!",
             "Everything you need for the caverns. Well, nearly everything. I'm out of courage. Try a Heart Berry."],
            ["What will it be today, friend?"]),
        2: (["Two Glowspores! The night moths are scared off, and my customers are back. Bless you, Pip."],
            ["What will it be today, friend?"]),
        3: (["Heading up to the Canopy? Glide Seeds are flying off the shelf. Ha! Flying!"],
            ["What will it be today, friend?"]),
        4: (["Duskmantle's lair, eh? For the hero of Hearthollow I'd offer a discount... but a chanterelle has to eat."],
            ["Last stop before the Hollow. Stock up, friend!"]),
        6: (["The hero of Hearthollow! Every coin you spend here goes into my festival fund. Spend generously!"],
            ["Festival prices! Same as the old prices. Don't tell anyone."]),
    },
    3: {  # Bram: snail postman, gameplay tips
        0: (["Spe...cial... de...livery. Oh, it's you, Pip. No letters today. Only advice.",
             "Hold jump longer to leap higher, and hold Shift to run. Even I know that, and I am a snail.",
             "Stomp acorns from above. From the side? Ouch. Trust me."],
            ["Tip: Sun Medals hide three to a level. The map remembers the most you have found."]),
        1: (["Rootdeep tip: cracked blocks wobble before they fall. Do not dawdle. I always dawdle. That is why I avoid caves.",
             "Bats hang still until you come close. The Glow Grubs light the dark, so follow them."],
            ["Tip: the beetle in the caverns flips over after hitting a wall. That is when you stomp."]),
        2: (["Grotto tip: in the water, press jump to swim upward. Bloatfins puff up when scared: never stomp a puffed Bloatfin.",
             "Driftjellies can't be stomped at all. Spore Puff shots work, though. Chanterelle sells them."],
            ["Tip: currents push you around. Swim along with them, not against them."]),
        3: (["Canopy tip: when the leaves start swirling, a gust is coming. Brace yourself, or ride it.",
             "With a Glide Seed, hold jump while falling to float. I tried one once. It took me three days to land."],
            ["Tip: moving leaves carry you over the gaps. Wait for one. There is always another."]),
        4: (["Hollow tip: Duskmantle gets dizzy after a swoop and lands. That is your moment to stomp.",
             "A Bark Shield takes one hit for you. I keep mine on my back. Well. I am mostly back."],
            ["Tip: when she drops Gloom Orbs, keep moving. Standing still is my job, not yours."]),
        6: (["The mail is finally moving again. Two hundred letters, and every one of them says thank you. To you.",
             "I will deliver them all by next spring. Probably."],
            ["Another letter for you. It says thank you. They all say thank you."]),
    },
    4: {  # Puffball: tiny kid, cute lines + a secret hint
        0: (["Pip! Pip! Are you going on an adventure? Can I come? No? Okay.",
             "Psst. Secret! In Mushroom Meadow, climb the big mushroom caps. There's a Sun Medal way, way up high!"],
            ["Psst. The bouncy red mushrooms throw you super high. Like, SUPER high."]),
        1: (["You got one! It's so shiny. I named it Glowy.",
             "Psst. Secret! In the caverns, some cracked rocks crumble. Bonk the ceilings and see what falls out!"],
            ["Psst. Bonk every question box. Sometimes there's a power-up inside!"]),
        2: (["I practised stomping today. On a mushroom. It stomped back.",
             "Psst. Secret! In the Grotto, the treasure isn't always at the bottom. Swim up into the old ruins!"],
            ["Psst. Bubbles mean something is hiding nearby. Probably. Maybe. I made that up."]),
        3: (["When I grow up I'm going to be a Glowspore. Mama says that's not how it works.",
             "Psst. Secret! In the Canopy, the highest leaf leads to a nest full of coins!"],
            ["Psst. Glide Seeds make you float like a dandelion. I want one SO bad."]),
        4: (["Are you scared of Duskmantle? I'm not. Okay, a little. Okay, a lot.",
             "Psst. Secret! Duskmantle hates spores. Bring a Spore Puff and throw, throw, throw!"],
            ["Psst. When she lands all dizzy, jump on her head! Then run!"]),
        6: (["You did it! You did it! I'm telling everyone. I already told everyone. I'm telling them again!"],
            ["When I grow up I'm going to be YOU."]),
    },
    5: {  # Old Wick: lamplighter, remarks how many Glowspores have returned
        0: (["Forty years I've lit these lamps, and never once seen the Tree go dark. Not once. Until now.",
             "Not a single Glowspore up there. Just me and my little lamps, against the whole long night."],
            ["Bring a light back to that Tree, youngster. My lamps can't hold the dark forever."]),
        1: (["One Glowspore back in the Tree. One! I lit one lamp less tonight. Felt like a holiday."],
            ["One down, four to go. I'm counting. I'm always counting."]),
        2: (["Two Glowspores glowing up there. The dusk moths keep their distance now. Cowards. Good."],
            ["Two lights. Two! Don't let it go to your head, hat and all."]),
        3: (["Three Glowspores. More than half! My old knees can feel the warmth coming back."],
            ["Three. I polished my lamps anyway. Just in case."]),
        4: (["Four Glowspores. The Tree's nearly whole. Bring back the last one and I'll retire.",
             "...No, I won't. But I'll want to."],
            ["One more, youngster. One more."]),
        5: (["All five Glowspores home. Look at that glow. Forty years, and I never saw it shine so bright."],
            ["Sit with me a while. Watch the Tree."]),
        6: (["All five Glowspores, burning bright. Tonight, for the first time in forty years, I'm leaving my lamps unlit.",
             "Well. Maybe just the one. Old habits."],
            ["Sit with me a while. Watch the Tree. Best show in the valley."]),
    },
}

# Reputation-aware extras (hidden lore: hinted, never explained). Keyed by NPC, then taint stage:
# stage 0 is only used while Pip is "kind" (Reputation Kit RepIsKind: almost no brutality, a few stomps).
# (mode, pages): "before" goes in front of the story lines, "after" behind them, "replace" instead of them.
# "@" starts a page spoken by Pip. Puffball hides from stage 2 on (NPC Puffball) and only answers muffled.
REP_LINES = {
    1: {
        0: ("after", ["Word reaches even an old man's ears, Pip: you hop on the acorns and leave them be. That is the way of a true hero."]),
        1: ("before", ["Hm. Pip. You smell of crushed leaves and... something else.",
                       "Old tales say a cap remembers what its wearer does. Tales, only tales. Forget I spoke."]),
        2: ("before", ["Pip... your cap. It was red as a ripe berry once. Now it is the colour of a bruise.",
                       "@It is just the light in here, Elder.",
                       "Perhaps. Come, sit by the Tree and rest a while. Please. Let the night pass without you."]),
        3: ("replace", ["Pip. Look at me. Your cap is nearly black, and the spots on it are glowing.",
                        "@It is only a hat, Elder.",
                        "Is it? Sit. Rest. Let the Tree's light fall on you. Do not go out again tonight, little one. I beg you."]),
    },
    2: {
        0: ("after", ["And for a friend who never hurts a soul, a little off the top! Look at those prices."]),
        1: ("before", ["Oh! Pip. Hello. Business as usual. Perfectly usual."]),
        2: ("before", ["P-Pip! Of course, of course. Take what you need. The prices are... as marked. Only as marked."]),
        3: ("replace", ["D-don't hurt me. The whole stall is yours, take what you like.",
                        "Only... don't look at me like that. Your cap is humming."]),
    },
    3: {
        0: ("after", ["I watched you bop an acorn and walk on by. Gentle. Even my shell approves."]),
        1: ("before", ["Ah. Pip. I heard about the acorn. And the other acorn. And the beetle."]),
        2: ("before", ["Slowly. I always move slowly. You have noticed. Please do not step on me."]),
        3: ("replace", ["Sp-special... no. No letters. No tips.", "I am going now. Slowly, yes. But going."]),
    },
    4: {
        0: ("after", ["Everybody says you are SO nice to the acorns! Can I be nice like you? Pretty please?"]),
        1: ("before", ["Hi Pip. Is that... never mind. I am not looking. I am looking at the ground."]),
        2: ("replace", ["(From behind a barrel, muffled) ...go away, please...",
                        "(muffled) ...the spots are looking at me... I did not do anything..."]),
        3: ("replace", ["(A whisper from behind the barrel) ...please... please... please..."]),
    },
    5: {
        0: ("after", ["Not a mean bone in you, youngster. That cap shines like a freshly trimmed lamp."]),
        1: ("before", ["Your cap is deeper in colour than I remember. Mushrooms do that when the night runs long."]),
        2: ("before", ["Hm hm hmmm... hm-hm... Do you hear that tune? It is coming from your cap, youngster.",
                       "@I do not hear anything.",
                       "No. I suppose you would not."]),
        3: ("replace", ["Hm-hm-hm... the tune is louder now. It knows all the words.",
                        "Forty years of lamps, and I know when something under a hat is humming back.",
                        "@Please, stop humming.",
                        "That is not me, youngster. That has never been me."]),
    },
}


def rep_function_lines():
    out = [
        "",
        "// HubRepPages(npc, base) -> the pages to speak: `base` (HubVillageLines) with the reputation extras",
        "// (REP_LINES in hub_gen.py) before, after or instead of it. Unchanged without the Reputation Kit.",
        "function HubRepPages(npc, base) {",
        "    if (!ResourceExists(\"Reputation Kit\")) { return base; }",
        "    var rstage = RepStage();",
        "    var kind = RepIsKind();",
    ]
    for npc in sorted(REP_LINES):
        out.append("    if (npc == %d) {" % npc)
        for rs in sorted(REP_LINES[npc]):
            mode, texts = REP_LINES[npc][rs]
            pages = "~".join(pages_of(texts))
            cond = "rstage == %d" % rs
            if rs == 0:
                cond = "rstage == 0 && kind == 1"
            if mode == "before":
                body = "return StringJoin3(\"%s\", \"~\", base);" % pages
            elif mode == "after":
                body = "return StringJoin3(base, \"~\", \"%s\");" % pages
            else:
                body = "return \"%s\";" % pages
            out.append("        if (%s) { %s }" % (cond, body))
        out.append("    }")
    out.append("    return base;")
    out.append("}")
    return out


SHOP_INFO = [
    "Heart Berry: grow your heart. One more maximum heart, up to six in all.",
    "Extra Life: one more try, freshly bottled. Don't ask how.",
    "Glide Seed: hold jump while falling to float gently down.",
    "Spore Puff: press the action key to throw bouncing spores.",
    "Bark Shield: a sturdy oak ring that takes one hit for you.",
    "Sword: swing it with the Use button. Two damage, and it cuts clean through.",
    "Shield: hold Use to raise it. Blocks hits from the front, and bashes.",
    "Bow: hold Use to draw, let go to fire. Comes with a dozen arrows.",
    "Arrows x10: ammunition for a bow. A quiver holds ninety-nine.",
    "Propeller Flower: Use it in the air to float down. Hold Jump to rise!",
    "Off you go? Come back soon, friend, and bring coins!",
]
SHOP_BOUGHT = [
    "A Heart Berry! Crunchy, sweet, and good for courage. One more heart for you.",
    "One extra life, freshly bottled. Trade secret. Use it wisely!",
    "A Glide Seed! Hold jump while falling and float like dandelion fluff.",
    "Spore Puff! Press action to throw bouncy spores. Aim for the jellies.",
    "A Bark Shield. Sturdy oak, takes one hit for you. Knock on wood!",
    "A fine Sword! Mind the edge. Sharp things keep their promises.",
    "A Shield, good and solid. Hold Use to raise it, friend.",
    "A Bow, and a dozen arrows to go with it. Aim true!",
    "Ten arrows, tied with string. Don't spend them all in one place.",
    "A Propeller Flower! Open it in the air and let the wind do the work.",
]
SHOP_WELCOME = "Take your time, friend. Everything is fresh from the forest floor."
SHOP_POOR = "Ah... you're a few coins short, friend. Come back after a stroll through the meadow."
SHOP_FULL = "Your heart is as big as it gets, Pip! Six whole hearts."
SHOP_HAVE = "You're already carrying one of those. Save your coins!"
SHOP_REFUSE = "Oh... no, no. Not that one, Pip. I am out of stock. Entirely out."
SHOP_QUIVER = "Your quiver is full to the brim, friend. Ninety-nine arrows!"
SHOP_KIND = "Friends get friendly prices. Take your time, Pip!"
SHOP_UNEASY = "Take... take what you need, Pip. Please. Just take it."


def wrap_lines(text):
    for ch in "~/\"\\":
        assert ch not in text, "reserved character %r in: %s" % (ch, text)
    lines = textwrap.wrap(text, WRAP)
    assert lines, text
    return lines


def pages_of(texts):
    """List of page strings ('a/b/c'), long pages continued on the next page, '@' kept."""
    out = []
    for raw in texts:
        pip = raw.startswith("@")
        body = raw[1:] if pip else raw
        lines = wrap_lines(body)
        for i in range(0, len(lines), 3):
            out.append(("@" if pip else "") + "/".join(lines[i:i + 3]))
    return out


def wrapped(text):
    lines = wrap_lines(text)
    assert len(lines) <= 3, text
    return "/".join(lines)


def build_lines_script():
    out = [
        "// Village Lines: every line the Hearthollow villagers say, generated by",
        "// BuildTools/TemplateContent/MushroomMeadow/hub_gen.py (edit the LINES table there and re-run).",
        "// HubVillageLines(npc, stage, again) -> pages joined by \"~\", lines joined by \"/\";",
        "// a page starting with \"@\" is spoken by Pip.",
        "// npc: 1 Elder Morel, 2 Chanterelle, 3 Bram, 4 Puffball, 5 Old Wick.",
        "// stage: Glowspores returned (0-5), or 6 after the ending. again: 1 on a repeat visit.",
        "function HubVillageLines(npc, stage, again) {",
    ]
    for npc in sorted(LINES):
        table = LINES[npc]
        out.append("    if (npc == %d) {" % npc)
        last = None
        for stage in range(7):
            entry = table.get(stage, last)
            last = entry
            first, repeat = entry
            out.append("        if (stage == %d) {" % stage)
            out.append("            if (again == 1) { return \"%s\"; }" % "~".join(pages_of(repeat)))
            out.append("            return \"%s\";" % "~".join(pages_of(first)))
            out.append("        }")
        out.append("    }")
    out.append("    return \"...\";")
    out.append("}")
    out += rep_function_lines()
    write_text("Scripts/Village Lines.pgsl", "\n".join(out) + "\n")
    write_meta("Scripts/Village Lines.pgsl", "Script", "Village Lines")


HUB_COMMON = """// Hub Common: helpers shared by the Overworld and Hearthollow objects (library functions only).
// Campaign globals (see the game's design notes): clear1..clear5, medals1..medals5, glowspores,
// mapNode, coins, lives, hearts, maxHearts, power, campaign, slot, musicVol, sfxVol, crt.

// Node k (0 Hearthollow, 1-5 the levels) is open when the level before it is cleared.
function HubNodeOpen(k) {
    if (k <= 1) { return 1; }
    if (GlobalGet("clear" + (k - 1)) == 1) { return 1; }
    return 0;
}

function HubNodeCleared(k) {
    if (k < 1) { return 0; }
    if (GlobalGet("clear" + k) == 1) { return 1; }
    return 0;
}

// Glowspores returned: the global, or the cleared levels if those say more.
function HubGlowCount() {
    var n = 0;
    for (var k = 1; k <= 5; k += 1) {
        if (GlobalGet("clear" + k) == 1) { n += 1; }
    }
    return Max(n, Clamp(GlobalGet("glowspores"), 0, 5));
}

// A hub room started on its own (F5) has no campaign globals yet: give it a fresh adventurer.
// Returns 1 when it had to fill them in.
function HubEnsureDefaults() {
    var filled = 0;
    if (!GlobalExists("lives")) { GlobalSet("lives", 5); filled = 1; }
    if (!GlobalExists("coins")) { GlobalSet("coins", 0); }
    if (!GlobalExists("maxHearts")) { GlobalSet("maxHearts", 3); }
    if (!GlobalExists("hearts")) { GlobalSet("hearts", GlobalGet("maxHearts")); }
    if (!GlobalExists("power")) { GlobalSet("power", 0); }
    if (!GlobalExists("glowspores")) { GlobalSet("glowspores", 0); }
    if (!GlobalExists("mapNode")) { GlobalSet("mapNode", 0); }
    return filled;
}

// Options chosen in the Main Menu (the "Apply Options" script loads them on a direct start and
// sets the buses and CRT), applied again in case this room was the first one run.
function HubApplyOptions() {
    if (ResourceExists("Apply Options")) {
        ScriptExecute("Apply Options");
        return 1;
    }
    if (GlobalExists("musicVol")) { SetBusVolume("music", Clamp(GlobalGet("musicVol"), 0, 1)); }
    if (GlobalExists("sfxVol")) { SetBusVolume("sfx", Clamp(GlobalGet("sfxVol"), 0, 1)); }
    if (GlobalExists("crt")) { PostEffectSetParameter("SNES Look", "Scanlines", GlobalGet("crt")); }
    return 0;
}

// Writes the campaign to its save slot through the Main Menu's "Game Save" script, when there is
// a campaign, a slot and the script; chime 1 also plays the Save Chime. Returns 1 when it saved.
function HubSave(chime) {
    if (GlobalGet("campaign") != 1) { return 0; }
    if (GlobalGet("slot") < 1) { return 0; }
    if (!ResourceExists("Game Save")) { return 0; }
    if (ScriptExecute("Game Save", chime)) { return 1; }
    return 0;
}

// Reputation Kit helpers (all 0 / unchanged without the kit).
function HubRepStage() {
    if (ResourceExists("Reputation Kit")) { return RepStage(); }
    return 0;
}

function HubRepKind() {
    if (ResourceExists("Reputation Kit")) { return RepIsKind(); }
    return 0;
}

// A shop price after the reputation multiplier (whole coins).
function HubShopPrice(base) {
    if (ResourceExists("Reputation Kit")) { return RepPrice(base); }
    return base;
}

// The shop multiplier as a percent (-25 .. +60).
function HubShopPercent() {
    if (ResourceExists("Reputation Kit")) { return RepPricePercent(); }
    return 0;
}

function HubConfirmPressed() {
    return KeyPressed("Enter") || KeyPressed("Space") || GamepadPressed("A");
}

function HubActionPressed() {
    return KeyPressed("E") || KeyPressed("J") || KeyPressed("X") || GamepadPressed("X");
}

function HubBackPressed() {
    return KeyPressed("Escape") || GamepadPressed("B");
}

function HubMenuPressed() {
    return KeyPressed("Escape") || KeyPressed("P") || GamepadPressed("Start");
}

// The left stick as a direction: 0 none, 1 up, 2 down, 3 left, 4 right.
function HubStickDir() {
    var ax = GamepadAxis("LeftX"); var ay = GamepadAxis("LeftY");
    if (ay > 0.6) { return 1; }
    if (ay < -0.6) { return 2; }
    if (ax < -0.6) { return 3; }
    if (ax > 0.6) { return 4; }
    return 0;
}

// A point at distance `along` on a trail: lists of x and y and the distance at each point.
// axis 0 returns x, 1 returns y.
function HubPathAt(px, py, pc, along, axis) {
    var n = PgListSize(pc);
    var i = 1;
    while (i < n - 1 && pc[i] < along) { i += 1; }
    var span = Max(0.001, pc[i] - pc[i - 1]);
    var f = Clamp((along - pc[i - 1]) / span, 0, 1);
    if (axis == 0) { return Lerp(px[i - 1], px[i], f); }
    return Lerp(py[i - 1], py[i], f);
}

// Room point -> GUI pixel, read from the view itself (Level Rules' RulesFitView lays the view out in the window): the scale is
// the port's width over the world width it shows, the offset is the port's corner.
function HubGuiX(wx) {
    var hbw = ViewGetBoundsW(0);
    if (hbw <= 0) { hbw = 640; }
    return ViewGetViewportX(0) + (wx - ViewGetPosX(0)) * (ViewGetViewportW(0) / hbw);
}

function HubGuiY(wy) {
    var hbw = ViewGetBoundsW(0);
    if (hbw <= 0) { hbw = 640; }
    return ViewGetViewportY(0) + (wy - ViewGetPosY(0)) * (ViewGetViewportW(0) / hbw);
}

// Text with a 1-pixel plum drop shadow, in the current alignment.
function HubShadowText(tx, ty, words, size, r, g, b) {
    var o = Max(1, size / 12);
    DrawSetColorRgb(42, 29, 46); DrawTextScaled(tx + o, ty + o, words, size);
    DrawSetColorRgb(r, g, b); DrawTextScaled(tx, ty, words, size);
}

// A speech bubble with a key letter in it, centred on (bx, by) = the bubble's bottom middle.
function HubPromptBubble(bx, by, key, label, s) {
    var w = Max(18 * s, DrawTextWidth(key, 22 * s) / 2 + 10 * s);
    DrawSetAlpha(0.96);
    DrawSetColorRgb(42, 29, 46); DrawRoundRectEx(bx - w - 3 * s, by - 34 * s, bx + w + 3 * s, by, 9 * s, 0);
    DrawSetColorRgb(255, 244, 214); DrawRoundRectEx(bx - w, by - 31 * s, bx + w, by - 3 * s, 7 * s, 0);
    DrawSetColorRgb(42, 29, 46); DrawRoundRectEx(bx - 4 * s, by - 2 * s, bx + 4 * s, by + 5 * s, 2 * s, 0);
    DrawSetTextAlign("center");
    DrawSetColorRgb(216, 67, 59); DrawTextScaled(bx, by - 31 * s, key, 22 * s);
    if (!StringEquals(label, "")) { HubShadowText(bx, by - 58 * s, label, 18 * s, 255, 235, 185); }
    DrawSetTextAlign("left");
    DrawSetAlpha(1);
}
"""


# ====================================================================== overworld objects
NODES = [(90, 250), (190, 205), (300, 250), (395, 292), (470, 170), (570, 88)]
NODE_NAMES = ["Hearthollow", "Mushroom Meadow", "Rootdeep Caverns", "Tidepool Grotto", "Windswept Canopy",
              "Duskmantle Hollow"]
NODE_INFO = ["Village hub: friends, a shop and the Lantern Tree",
             "Stage 1: sunny trails and a summit shrine",
             "Stage 2: dark tunnels, crystals and a grumbly beetle",
             "Stage 3: an undersea grotto of currents and jellies",
             "Stage 4: golden treetops, gusts and long glides",
             "Stage 5: the twilight nest of Duskmantle"]


def pgsl_list(values):
    def one(v):
        if isinstance(v, str):
            return '"%s"' % v
        if isinstance(v, list):
            return pgsl_list(v)
        if isinstance(v, float):
            return ("%.1f" % v).rstrip("0").rstrip(".")
        return str(v)
    return "[" + ", ".join(one(v) for v in values) + "]"


def trace_map_paths():
    """The Overworld Map's painted trails between consecutive nodes, as polylines.

    ART-SCENES paints each trail as 2x2 cream dots (#FFF4D6). Starting at a node, the nearest unused
    dot within 22 px is taken until the next node is reached. Returns (xs, ys, cumulative lengths) per
    segment; a segment that cannot be traced (or a missing map) is a straight line.
    """
    frames = os.path.join(ASSETS, "Sprites", "Overworld Map.spritedata", "frames")
    dots = []
    try:
        from PIL import Image
        names = sorted(os.listdir(frames))
        im = Image.open(os.path.join(frames, names[0])).convert("RGB")
        w, h = im.size
        px = im.load()
        seen = set()
        for y in range(h):
            for x in range(w):
                if (x, y) in seen or px[x, y] != (0xFF, 0xF4, 0xD6):
                    continue
                stack, blob = [(x, y)], []
                seen.add((x, y))
                while stack:
                    cx, cy = stack.pop()
                    blob.append((cx, cy))
                    for nx, ny in ((cx + 1, cy), (cx - 1, cy), (cx, cy + 1), (cx, cy - 1)):
                        if 0 <= nx < w and 0 <= ny < h and (nx, ny) not in seen and px[nx, ny] == (0xFF, 0xF4, 0xD6):
                            seen.add((nx, ny))
                            stack.append((nx, ny))
                if len(blob) <= 8:
                    dots.append((sum(p[0] for p in blob) / len(blob), sum(p[1] for p in blob) / len(blob)))
    except (ImportError, OSError, IndexError):
        dots = []

    def d2(a, b):
        return (a[0] - b[0]) ** 2 + (a[1] - b[1]) ** 2

    used, out = set(), []
    for k in range(5):
        a, b = NODES[k], NODES[k + 1]
        chain, cur = [a], a
        for _ in range(200):
            if len(chain) > 2 and d2(cur, b) < 20 ** 2:
                break
            reach = 26 if cur == a else 22
            cands = [i for i, p in enumerate(dots) if i not in used and d2(cur, p) < reach ** 2]
            if not cands:
                break
            if cur == a:
                cands.sort(key=lambda i: d2(dots[i], b))  # leave the node towards the next one
            else:
                cands.sort(key=lambda i: d2(cur, dots[i]))
            used.add(cands[0])
            cur = dots[cands[0]]
            chain.append(cur)
        if len(chain) < 4:
            chain = [a]
        chain.append(b)
        xs = [round(p[0], 1) for p in chain]
        ys = [round(p[1], 1) for p in chain]
        cum = [0.0]
        for i in range(1, len(chain)):
            cum.append(round(cum[-1] + ((xs[i] - xs[i - 1]) ** 2 + (ys[i] - ys[i - 1]) ** 2) ** 0.5, 1))
        out.append((xs, ys, cum))
        print("  map trail %d -> %d: %d points, %.0f px" % (k, k + 1, len(chain), cum[-1]))
    return out


MARKER_CREATE = """// Map Marker: Pip on the valley map, and the Overworld's controller.
// Arrows / WASD / d-pad / left stick walk along the paths between open nodes; Enter / Space / A
// enters the node's room; Esc / P / Start opens the map menu. Coming back from a cleared level
// (global returnFromLevel) plays the unlock reveal, walks to the new node and saves.
nodeX = @NODEX@;
nodeY = @NODEY@;
nodeName = @NAMES@;
nodeInfo = @INFO@;
// The painted trails between node k and k+1 (traced from the Overworld Map by hub_gen.py):
// points and the distance along the trail at each point.
pathX = @PATHX@;
pathY = @PATHY@;
pathC = @PATHC@;
var fresh = HubEnsureDefaults();
// Being on the map means playing the campaign: levels report back here when cleared.
GlobalSet("campaign", 1);
HubApplyOptions();
state = 0; t = 0; walking = 0; walkFrom = 0; walkTo = 0; walkT = 0; walkLen = 1; walkSeg = 0; stepTimer = 0;
pendingWalk = -1; stickLatch = 0; menuSel = 0; fadeT = 0; target = ""; gone = 0;
// Journal Kit (live): the completion percentage shown under the purse, and the Collection / How to Play pages.
jrnLive = 1; jrnFields = JrnFieldList(); completion = JrnRefresh();
toastT = 0; toastText = ""; seqT = 0; seqPhase = 0; seqFrom = 0; seqTo = -1; sparkT = 0;
revealSeg = -1; revealProg = 0; revealFade = 0; saveNow = 0;
node = Clamp(Round(GlobalGet("mapNode")), 0, 5);
// Never stand on a node that is still locked (an old save or a stray global).
while (node > 0 && HubNodeOpen(node) == 0) { node -= 1; }
var highest = 0;
for (var k = 1; k <= 5; k += 1) {
    if (HubNodeOpen(k) == 1) { highest = k; }
}
var revealed = highest;
if (GlobalExists("mapRevealed")) { revealed = GlobalGet("mapRevealed"); }
RoomValueSet("pendingUnlock", -1);
var back = Round(GlobalGet("returnFromLevel"));
GlobalSet("returnFromLevel", 0);
if (back >= 1 && back <= 5) {
    node = back; seqFrom = back; seqTo = -1; state = 2; seqT = 0; seqPhase = 0;
    // The path to the next node lights up only the first time it opens.
    if (back < 5 && HubNodeOpen(back + 1) == 1 && revealed < back + 1) {
        seqTo = back + 1;
        RoomValueSet("pendingUnlock", seqTo);
    }
} else {
    saveNow = 1;
}
GlobalSet("mapRevealed", highest);
GlobalSet("mapNode", node);
x = nodeX[node]; y = nodeY[node] + 2;
SpriteSet("Assets/Sprites/Pip Map.image.json"); AnimationPlay("Walk", true); AnimationSetSpeed(0);
SpriteSetDepth(-20);
music = PlaySound("Assets/Audio/Overworld Theme.audio.json", 0.7, 1, true);
"""

MARKER_STEP = """var dt = Clamp(DeltaTime, 0, 0.05);
t += dt;
AchToastStep(dt);
if (GlobalGet("campaign") == 1) { GlobalSet("playTime", GlobalGet("playTime") + dt); }
// 5: the Collection or How to Play page (Journal Kit) opened from the map menu.
if (state == 5) {
    JrnStep();
    if (JrnIsOpen() == 0) { state = 3; }
    return;
}
toastT = Max(0, toastT - dt);
revealFade = Max(0, revealFade - dt);
if (saveNow == 1) {
    saveNow = 0;
    if (HubSave(1) == 1) { toastText = "PROGRESS SAVED"; toastT = 2.2; }
}
var stick = HubStickDir();
var stickNew = 0;
if (stick != stickLatch) { stickNew = stick; }
stickLatch = stick;

// 4: walking into a room behind a fade.
if (state == 4) {
    fadeT += dt;
    if (fadeT >= 0.45 && gone == 0) {
        gone = 1;
        RoomChangeFade(0.4);
        RoomGoto(target);
    }
    return;
}

// 3: the map menu.
if (state == 3) {
    var moveUp = KeyPressed("Up") || KeyPressed("W") || GamepadPressed("Up") || stickNew == 1;
    var moveDown = KeyPressed("Down") || KeyPressed("S") || GamepadPressed("Down") || stickNew == 2;
    if (moveUp) { menuSel = (menuSel + 3) % 4; }
    if (moveDown) { menuSel = (menuSel + 1) % 4; }
    if (moveUp || moveDown) {
        PlaySound("Assets/Audio/Menu Move.audio.json", 0.6, 1, false);
    }
    if (HubMenuPressed() || HubBackPressed()) {
        state = 0;
        PlaySound("Assets/Audio/Menu Back.audio.json", 0.6, 1, false);
        return;
    }
    if (HubConfirmPressed() || HubActionPressed()) {
        PlaySound("Assets/Audio/Menu Select.audio.json", 0.7, 1, false);
        if (menuSel == 0) { state = 0; return; }
        if (menuSel == 1) { JrnOpen(1, 1, Clamp(GlobalGet("slot"), 1, 3)); state = 5; return; }
        if (menuSel == 2) { JrnOpen(1, 0, 1); state = 5; return; }
        HubSave(1);
        if (ResourceExists("Main Menu")) {
            target = "Main Menu"; state = 4; fadeT = 0;
            SoundFade(music, 0, 0.45);
        } else {
            state = 0; toastText = "NO TITLE SCREEN YET"; toastT = 2;
        }
    }
    return;
}

// 2: returning with a Glowspore: burst, reveal the path, open the node, walk there, save.
if (state == 2) {
    seqT += dt;
    if (seqPhase == 0 && seqT >= 0.35) {
        seqPhase = 1; seqT = 0;
        ParticleBurstAt("Assets/Particles/Map Unlock Burst.particle.json", nodeX[seqFrom], nodeY[seqFrom] - 6, 0, 1, 0);
        PlaySound("Assets/Audio/Power Up.audio.json", 0.55, 1.1, false);
    }
    if (seqPhase == 1 && seqT >= 0.85) {
        seqT = 0;
        if (seqTo < 0) {
            state = 0; saveNow = 1;
        } else {
            seqPhase = 2; revealSeg = seqFrom; revealProg = 0; sparkT = 0;
            PlaySound("Assets/Audio/Map Unlock.audio.json", 0.9, 1, false);
        }
    }
    if (seqPhase == 2) {
        revealProg = Min(1, seqT / 1.5);
        sparkT -= dt;
        if (sparkT <= 0) {
            sparkT = 0.07;
            var rc = pathC[seqFrom];
            var rs = revealProg * rc[PgListSize(rc) - 1];
            var sx0 = HubPathAt(pathX[seqFrom], pathY[seqFrom], rc, rs, 0);
            var sy0 = HubPathAt(pathX[seqFrom], pathY[seqFrom], rc, rs, 1);
            ParticleBurstAt("Assets/Particles/Path Sparkle.particle.json", sx0, sy0, 0, 1, 0);
        }
        if (revealProg >= 1) {
            seqPhase = 3; seqT = 0; revealFade = 1.2;
            RoomValueSet("pendingUnlock", -1);
            ParticleBurstAt("Assets/Particles/Map Unlock Burst.particle.json", nodeX[seqTo], nodeY[seqTo] - 4, 0, 1.2, 0);
            PlaySound("Assets/Audio/Map Unlock.audio.json", 0.7, 1.25, false);
            toastText = StringJoin2(StringUpper(nodeName[seqTo]), " OPEN!"); toastT = 2.4;
        }
    }
    if (seqPhase == 3 && seqT >= 0.6) {
        seqPhase = 4; pendingWalk = seqTo;
    }
}

// Walking along a path between two nodes.
var want = -1;
if (pendingWalk >= 0 && walking == 0) { want = pendingWalk; pendingWalk = -1; }
if (state == 0 && walking == 0 && want < 0) {
    var ix = 0; var iy = 0;
    if (KeyPressed("Right") || KeyPressed("D") || GamepadPressed("Right") || stickNew == 4) { ix = 1; }
    if (KeyPressed("Left") || KeyPressed("A") || GamepadPressed("Left") || stickNew == 3) { ix = -1; }
    if (KeyPressed("Up") || KeyPressed("W") || GamepadPressed("Up") || stickNew == 1) { iy = -1; }
    if (KeyPressed("Down") || KeyPressed("S") || GamepadPressed("Down") || stickNew == 2) { iy = 1; }
    if (ix != 0 || iy != 0) {
        // The neighbour (previous or next node) whose path points most along the input.
        var il = Sqrt(ix * ix + iy * iy);
        var pick = -1; var bestDot = 0.2;
        for (var k = node - 1; k <= node + 1; k += 2) {
            if (k >= 0 && k <= 5) {
                var vx = nodeX[k] - nodeX[node]; var vy = nodeY[k] - nodeY[node];
                var dot = (vx * ix + vy * iy) / (Max(1, Sqrt(vx * vx + vy * vy)) * il);
                if (dot > bestDot) { bestDot = dot; pick = k; }
            }
        }
        if (pick >= 0) {
            if (HubNodeOpen(pick) == 1) { want = pick; }
            else {
                PlaySound("Assets/Audio/Menu Back.audio.json", 0.5, 0.8, false);
                toastText = "THE WAY IS STILL DARK"; toastT = 1.6;
            }
        }
    }
    if (HubConfirmPressed()) {
        var roomName = nodeName[node];
        if (ResourceExists(roomName)) {
            GlobalSet("mapNode", node);
            target = roomName; state = 4; fadeT = 0;
            PlaySound("Assets/Audio/Menu Select.audio.json", 0.8, 1, false);
            SoundFade(music, 0, 0.45);
            return;
        }
        PlaySound("Assets/Audio/Menu Back.audio.json", 0.5, 0.8, false);
        toastText = "COMING SOON"; toastT = 1.6;
    }
    if (HubMenuPressed()) {
        state = 3; menuSel = 0;
        PlaySound("Assets/Audio/Menu Select.audio.json", 0.6, 1, false);
        return;
    }
}
if (want >= 0) {
    walkFrom = node; walkTo = want; walkT = 0; walking = 1; stepTimer = 0;
    walkSeg = Min(walkFrom, walkTo);
    var wc = pathC[walkSeg];
    walkLen = Max(1, wc[PgListSize(wc) - 1]);
    AnimationPlay("Walk", true); AnimationSetSpeed(1);
}
if (walking == 1) {
    walkT = Min(1, walkT + dt * 96 / walkLen);
    // Along the painted trail, forwards or backwards.
    var along = walkT * walkLen;
    if (walkTo < walkFrom) { along = walkLen - along; }
    var oldX = x;
    x = HubPathAt(pathX[walkSeg], pathY[walkSeg], pathC[walkSeg], along, 0);
    y = HubPathAt(pathX[walkSeg], pathY[walkSeg], pathC[walkSeg], along, 1) + 2;
    if (x < oldX - 0.05) { image_xscale = -1; }
    if (x > oldX + 0.05) { image_xscale = 1; }
    stepTimer -= dt;
    if (stepTimer <= 0) {
        stepTimer = 0.24;
        PlaySound("Assets/Audio/Map Step.audio.json", 0.45, RandomRange(0.92, 1.08), false);
    }
    if (walkT >= 1) {
        walking = 0; node = walkTo;
        GlobalSet("mapNode", node);
        AnimationSetSpeed(0);
        if (state == 2) {
            state = 0; saveNow = 1;
        }
    }
} else {
    // Standing: a one-pixel breathing bob.
    x = nodeX[node];
    y = nodeY[node] + 2 - Floor(t * 1.6) % 2;
}
"""

MARKER_DRAWGUI = """// Overworld GUI: Glowspore count, node banner (name, best Sun Medals), purse, help bar, menu.
// The frames come from the Overworld HUD User Interface (1280x720 design space).
var sw = WindowGetWidth(); var sh = WindowGetHeight();
var sx = sw / 1280; var sy = sh / 720; var s = Min(sx, sy);
// The map is letterboxed by the room (one scale for the whole picture): room points land at the GUI area's corner plus
// their position at its scale (WindowGetGui*), not at a stretch of the whole window.
var gx = WindowGetGuiWidth() / 640; var gy = gx; var gox = WindowGetGuiX(); var goy = WindowGetGuiY();
var ui = "Assets/User Interfaces/Overworld HUD.ui.json";

// Trails: the painted dots of a trail that is still dark are dimmed; the trail being revealed
// lights up dot by dot behind the sparkles, then its glow fades back into the painting.
var pend = RoomValueGet("pendingUnlock", -1);
for (var k = 0; k < 5; k += 1) {
    var lit = HubNodeOpen(k + 1) == 1 && pend != k + 1;
    var glowing = revealSeg == k && (seqPhase == 2 || revealFade > 0);
    if (!lit || glowing) {
        var tx = pathX[k]; var ty = pathY[k]; var tc = pathC[k];
        var tn = PgListSize(tc); var tl = Max(1, tc[tn - 1]);
        var ga = 1;
        if (seqPhase != 2) { ga = Clamp(revealFade / 1.2, 0, 1); }
        for (var i = 1; i < tn - 1; i += 1) {
            var f = tc[i] / tl;
            var px = gox + tx[i] * gx; var py = goy + ty[i] * gy;
            if (glowing && (lit || f <= revealProg)) {
                DrawSetAlpha(ga * 0.45); DrawSetColorRgb(255, 214, 110); DrawCircleEx(px, py, 4.5 * s, 0);
                DrawSetAlpha(ga); DrawSetColorRgb(255, 250, 225); DrawCircleEx(px, py, 2.2 * s, 0);
            } else if (!lit) {
                DrawSetAlpha(0.6); DrawSetColorRgb(42, 29, 46); DrawCircleEx(px, py, 2.6 * s, 0);
            }
        }
    }
}
DrawSetAlpha(1);

var shown = node;
if (walking == 1) { shown = walkTo; }
var status = "VILLAGE";
if (shown > 0) {
    status = "NEW!";
    if (HubNodeCleared(shown) == 1) { status = "CLEARED"; }
    if (HubNodeOpen(shown) == 0 || RoomValueGet("pendingUnlock", -1) == shown) { status = "LOCKED"; }
}
var medals = GlobalGet("medals" + shown);
UiSetText(ui, "bannerTitle", StringUpper(nodeName[shown]));
UiSetText(ui, "bannerSub", nodeInfo[shown]);
UiSetText(ui, "statusText", status);
UiSetVisible(ui, "medalLabel", shown > 0);
UiSetVisible(ui, "medalPip1", shown > 0); UiSetVisible(ui, "medalFill1", shown > 0 && medals >= 1);
UiSetVisible(ui, "medalPip2", shown > 0); UiSetVisible(ui, "medalFill2", shown > 0 && medals >= 2);
UiSetVisible(ui, "medalPip3", shown > 0); UiSetVisible(ui, "medalFill3", shown > 0 && medals >= 3);
UiSetText(ui, "glowText", StringJoin2(StringOf(HubGlowCount()), " / 5"));
UiSetText(ui, "coinsText", StringOf(GlobalGet("coins")));
UiSetText(ui, "livesText", StringJoin2("x", StringOf(GlobalGet("lives"))));
UiSetVisible(ui, "toastPanel", toastT > 0); UiSetVisible(ui, "toastText", toastT > 0);
UiSetText(ui, "toastText", toastText);
var menuOn = state == 3;
UiSetVisible(ui, "menuShade", menuOn); UiSetVisible(ui, "menuPanel", menuOn); UiSetVisible(ui, "menuTitle", menuOn);
UiSetVisible(ui, "menuOpt0", menuOn); UiSetVisible(ui, "menuOpt1", menuOn); UiSetVisible(ui, "menuHint", menuOn);
UiSetVisible(ui, "menuOpt2", menuOn); UiSetVisible(ui, "menuOpt3", menuOn);
DrawUi(ui);

// Level select feel: the best time of the level under the marker (right of the medal pips) and the game's completion.
if (shown > 0 && HubNodeCleared(shown) == 1) {
    var bestSec = GlobalGet("bestTime" + shown);
    if (bestSec > 0) {
        DrawSetTextAlign("center");
        HubShadowText(776 * sx, 104 * sy, StringJoin2("BEST ", JrnClock(bestSec)), 18 * s, 255, 235, 185);
        DrawSetTextAlign("left");
    }
}
DrawSetTextAlign("left");
DrawSetAlpha(0.82); DrawSetColorRgb(29, 48, 67);
DrawRoundRectEx(20 * sx, 86 * sy, 236 * sx, 112 * sy, 8 * s, 0);
DrawSetAlpha(0.9); DrawSetColorRgb(255, 210, 63);
DrawRoundRectEx(20 * sx, 86 * sy, 236 * sx, 112 * sy, 8 * s, 2 * s);
DrawSetAlpha(1);
HubShadowText(32 * sx, 88 * sy, StringJoin3("COMPLETION ", StringOf(completion), "%"), 18 * s, 255, 235, 185);

if (menuOn) {
    var rowY = 288 + menuSel * 54;
    var pulse = 0.7 + 0.3 * Sin(t * 360);
    DrawSetAlpha(pulse); DrawSetColorRgb(255, 210, 63);
    DrawRoundRectEx(452 * sx, (rowY + 2) * sy, 828 * sx, (rowY + 42) * sy, 8 * s, 3 * s);
    DrawSetAlpha(1);
    HubShadowText(462 * sx, (rowY + 8) * sy, ">", 26 * s, 255, 223, 145);
}
if (state == 4) {
    DrawSetAlpha(Clamp(fadeT / 0.45, 0, 1)); DrawSetColorRgb(16, 12, 24);
    DrawRectangle(0, 0, sw, sh);
}
if (state == 5) { JrnDraw(); }
AchToastDraw("Consolas");
DrawSetAlpha(1); DrawSetColorRgb(255, 255, 255);
"""

NODE_CREATE = """// Map Node: one stop on the valley map. It takes its index from where the room placed it
// (nearest of the six node spots) and shows Locked / Open / Cleared from the campaign globals.
// While the Map Marker reveals a newly opened path, RoomValue "pendingUnlock" keeps it Locked.
spotX = @NODEX@;
spotY = @NODEY@;
nodeIndex = 0;
var best = 999999;
for (var k = 0; k < 6; k += 1) {
    var d = Abs(x - spotX[k]) + Abs(y - spotY[k]);
    if (d < best) { best = d; nodeIndex = k; }
}
shownTag = ""; glow = -1; t = nodeIndex * 0.4;
SpriteSetDepth(-5);
AnimationPlay("Open", false); AnimationSetSpeed(0);
"""

NODE_STEP = """var dt = Clamp(DeltaTime, 0, 0.05);
t += dt;
var tag = "Locked";
if (nodeIndex == 0) { tag = "Open"; }
else if (RoomValueGet("pendingUnlock", -1) == nodeIndex) { tag = "Locked"; }
else if (HubNodeCleared(nodeIndex) == 1) { tag = "Cleared"; }
else if (HubNodeOpen(nodeIndex) == 1) { tag = "Open"; }
if (!StringEquals(tag, shownTag)) {
    shownTag = tag;
    AnimationPlay(tag, false); AnimationSetSpeed(0);
    // A cleared node keeps its returned Glowspore glowing.
    if (StringEquals(tag, "Cleared") && glow < 0) {
        glow = SpawnParticleEmitter("Assets/Particles/Node Glow.particle.json", x, y - 4, 0, 1);
    }
}
// The next open level breathes to catch the eye.
if (nodeIndex > 0 && StringEquals(shownTag, "Open")) {
    var p = 1 + 0.1 * Sin(t * 220);
    image_xscale = p; image_yscale = p;
} else {
    image_xscale = 1; image_yscale = 1;
}
"""

CLOUDS_CREATE = """// Map Clouds: a 1024 px strip of soft clouds drifting over the valley map. Two instances sit
// side by side and each wraps round when it has drifted a whole width off the left edge.
drift = 7; t = 0;
SpriteSetDepth(3000);
image_alpha = 0.85;
"""

CLOUDS_STEP = """var dt = Clamp(DeltaTime, 0, 0.05);
x -= drift * dt;
if (x <= -1024) { x += 2048; }
"""


# ====================================================================== village objects
NPCS = [
    # object name, sprite, key, head height (px above feet), wander radius
    ("NPC Elder Morel", "Elder Morel", 1, 48, 0),
    ("NPC Chanterelle", "Chanterelle", 2, 40, 0),
    ("NPC Bram", "Bram", 3, 32, 36),
    ("NPC Puffball", "Puffball", 4, 28, 0),
    ("NPC Old Wick", "Old Wick", 5, 44, 0),
]

NPC_CREATE = """// @OBJ@: a Hearthollow villager. Idle animation; when Pip stands close a talk bubble shows,
// and the action key (or the Explorer's RoomValue "talk") asks Village Host for the conversation
// (RoomValue "talkRequest" = @KEY@). The lines themselves are in the Village Lines script.
npcKey = @KEY@; npcName = "@NAME@"; headH = @HEAD@; wander = @WANDER@;
homeX = x; near = 0; talking = -1; t = @KEY@ * 0.37; dir = 1; pause = 0; hideKid = 0;
SpriteSet("Assets/Sprites/@SPRITE@.image.json");
AnimationPlay("Idle", true); AnimationSetSpeed(1);
"""

NPC_STEP = """var dt = Clamp(DeltaTime, 0, 0.05);
t += dt;
// Puffball is afraid of a feared Pip (taint stage 2 and up): he hides behind the barrels and is only
// heard, muffled (the lines are in Village Lines: HubRepPages).
if (npcKey == 4) {
    var hideNow = 0;
    if (HubRepStage() >= 2) { hideNow = 1; }
    if (hideNow != hideKid) { hideKid = hideNow; SpriteSetVisible(hideKid == 0); }
}
var hero = RoomValueGet("heroId", 0);
near = 0;
var hx = x;
if (hero > 0) {
    hx = InstanceGetX(hero);
    if (Abs(hx - x) <= 40 && Abs(InstanceGetY(hero) - y) <= 48) { near = 1; }
}
var nowTalking = 0;
if (RoomValueGet("talkingTo", 0) == npcKey) { nowTalking = 1; }
if (nowTalking != talking) {
    talking = nowTalking;
    if (talking == 1) { AnimationPlay("Talk", true); } else { AnimationPlay("Idle", true); }
}
// Bram makes his slow rounds; everyone stops to face Pip when Pip comes close.
if (wander > 0 && near == 0 && talking == 0) {
    pause = Max(0, pause - dt);
    if (pause <= 0) {
        x += dir * 9 * dt;
        if (x > homeX + wander) { x = homeX + wander; dir = -1; pause = 1.5; }
        if (x < homeX - wander) { x = homeX - wander; dir = 1; pause = 1.5; }
        image_xscale = dir;
    }
} else if (hero > 0 && Abs(hx - x) < 160) {
    if (hx < x - 2) { image_xscale = -1; }
    if (hx > x + 2) { image_xscale = 1; }
}
if (near == 1 && RoomValueGet("dialogue", 0) == 0) {
    if (RoomValueGet("talk", 0) == 1 || HubActionPressed()) { RoomValueSet("talkRequest", npcKey); }
}
"""

NPC_DRAWGUI = """// The talk prompt above the villager's head while Pip is close and no box is open.
if (near == 1 && RoomValueGet("dialogue", 0) == 0) {
    var s = Min(WindowGetWidth() / 1280, WindowGetHeight() / 720);
    var bob = Abs(Sin(t * 180)) * 4 * s;
    var label = npcName;
    if (hideKid == 1) { label = "..."; }
    HubPromptBubble(HubGuiX(x), HubGuiY(y - headH - 6) - bob, "E", label, s);
}
"""

HOST_CREATE = """// Village Host: Hearthollow's conversations, Chanterelle's shop and the way back to the map.
// Villagers ask with RoomValueSet("talkRequest", npc); the Exit Sign with RoomValueSet("leaveRequest", 1).
// While a box is open RoomValue "dialogue" is 1 (the Explorer stands still) and "talkingTo" names the
// villager speaking. Shop prices: Heart Berry 30, Extra Life 50, Glide Seed 20, Spore Puff 25, Bark Shield 25,
// Sword 40, Shield 35, Bow 60 (with 12 arrows), Arrows x10 10, Propeller Flower 50; all times the Reputation Kit's
// RepPriceMul (HubShopPrice). The weapons (Sword, Bow, Arrows) are refused at taint stage 3 (DREADED).
// The Explorer mirrors lives/coins/hearts/maxHearts/power into the globals every step; changes made
// here are handed to it with RoomValueSet("heroReload", 1).
var fresh = HubEnsureDefaults();
// Started on its own (F5): a small purse so the shop can be tried straight away (given in Step 1).
standalonePurse = fresh;
HubApplyOptions();
mode = 0; npc = 0; speaker = 0; pages = ""; pageCount = 0; page = 1; pageText = ""; lineCount = 1;
totalChars = 0; reveal = 0; lastBlip = 0; inputDelay = 0; closeTimer = 0; stickLatch = 0;
shopSel = 0; shopMsg = ""; shopMood = 0; leaving = 0; fadeT = 0; gone = 0; t = 0;
speakerName = ["Pip", "Elder Morel", "Chanterelle", "Bram", "Puffball", "Old Wick"];
voicePitch = [1.18, 0.78, 0.96, 0.66, 1.45, 0.88];
portraitId = ["portraitPip", "portraitMorel", "portraitChanterelle", "portraitBram", "portraitPuffball", "portraitWick",
              "portraitPipSpore1", "portraitPipSpore2", "portraitPipSpore3"];
itemName = ["Heart Berry", "Extra Life", "Glide Seed", "Spore Puff", "Bark Shield", "Sword", "Shield", "Bow",
            "Arrows x10", "Propeller Flower", "Leave"];
itemPrice = [30, 50, 20, 25, 25, 40, 35, 60, 10, 50, 0];
shopX = @SHOPX@;
shopY = @SHOPY@;
itemInfo = @INFO@;
itemBought = @BOUGHT@;
shopWelcome = "@WELCOME@";
shopPoor = "@POOR@";
shopFull = "@FULL@";
shopHave = "@HAVE@";
shopRefuse = "@REFUSE@";
shopQuiver = "@QUIVER@";
shopKind = "@KIND@";
shopUneasy = "@UNEASY@";
RoomValueSet("dialogue", 0); RoomValueSet("talkRequest", 0); RoomValueSet("leaveRequest", 0);
RoomValueSet("talkingTo", 0);
GlobalSet("mapNode", 0);
fireflies = SpawnParticleEmitter("Assets/Particles/Village Fireflies.particle.json", RoomGetWidth() / 2, 176, 0, 1);
"""

HOST_STEP = """function HostOpenPage() {
    var raw = StringSplitPart(self.pages, "~", self.page);
    self.speaker = self.npc;
    if (StringStartsWith(raw, "@")) { self.speaker = 0; raw = StringDelete(raw, 1, 1); }
    self.pageText = raw;
    self.lineCount = StringSplitCount(raw, "/");
    self.totalChars = StringLength(raw);
    self.reveal = 0; self.lastBlip = 0;
}

function HostClose() {
    self.mode = 0; self.closeTimer = 0.15;
    RoomValueSet("talkingTo", 0);
}

var dt = Clamp(DeltaTime, 0, 0.05);
t += dt;
if (standalonePurse == 1) {
    standalonePurse = 0;
    GlobalSet("coins", 60);
    RoomValueSet("heroReload", 1);
}
inputDelay = Max(0, inputDelay - dt);
if (closeTimer > 0) {
    closeTimer -= dt;
    RoomValueSet("talkRequest", 0);
    if (closeTimer <= 0) { closeTimer = 0; RoomValueSet("dialogue", 0); }
}
var stick = HubStickDir();
var stickNew = 0;
if (stick != stickLatch) { stickNew = stick; }
stickLatch = stick;

if (leaving == 1) {
    fadeT += dt;
    if (fadeT >= 0.5 && gone == 0) {
        gone = 1;
        GlobalSet("mapNode", 0);
        RoomChangeFade(0.4);
        RoomGoto("Overworld");
    }
    return;
}

if (mode == 0) {
    if (closeTimer > 0) { return; }
    if (RoomValueGet("leaveRequest", 0) == 1) {
        RoomValueSet("leaveRequest", 0);
        leaving = 1; fadeT = 0;
        RoomValueSet("dialogue", 1);
        PlaySound("Assets/Audio/Door.audio.json", 0.8, 1, false);
        return;
    }
    var req = RoomValueGet("talkRequest", 0);
    RoomValueSet("talkRequest", 0);
    if (req >= 1 && req <= 5) {
        npc = req;
        var stage = HubGlowCount();
        if (GlobalGet("gameComplete") == 1) { stage = 6; }
        var visits = GlobalGet("hubTalk" + npc);
        var again = 0;
        if (visits > 0) { again = 1; }
        GlobalSet("hubTalk" + npc, visits + 1);
        AchOnVillage();
        pages = HubVillageLines(npc, stage, again);
        pages = HubRepPages(npc, pages);
        pageCount = StringSplitCount(pages, "~");
        page = 1;
        HostOpenPage();
        mode = 1; inputDelay = 0.2;
        RoomValueSet("dialogue", 1); RoomValueSet("talkingTo", npc);
        PlaySound("Assets/Audio/Menu Select.audio.json", 0.45, 1.2, false);
    }
    return;
}

var advance = HubConfirmPressed() || HubActionPressed();
if (inputDelay > 0) { advance = 0; }

// 1: talking. Typewriter text with a blip in the speaker's own voice.
if (mode == 1) {
    var before = Floor(reveal);
    reveal = Min(totalChars, reveal + dt * 44);
    var now = Floor(reveal);
    if (now > before && now - lastBlip >= 2) {
        lastBlip = now;
        var ch = StringCharAt(pageText, now);
        if (!StringEquals(ch, " ") && !StringEquals(ch, "/")) {
            PlaySound("Assets/Audio/Dialogue Blip.audio.json", 0.32, voicePitch[speaker] * RandomRange(0.95, 1.05), false);
        }
    }
    if (HubBackPressed() && inputDelay <= 0) {
        HostClose();
        PlaySound("Assets/Audio/Menu Back.audio.json", 0.5, 1, false);
        return;
    }
    if (advance) {
        if (reveal < totalChars) {
            reveal = totalChars;
        } else if (page < pageCount) {
            page += 1;
            HostOpenPage();
            PlaySound("Assets/Audio/Menu Move.audio.json", 0.35, 1.1, false);
        } else if (npc == 2) {
            // Chanterelle opens the shop after the greeting.
            mode = 2; shopSel = 0; shopMsg = shopWelcome; shopMood = 0; inputDelay = 0.15;
            if (HubRepKind() == 1) { shopMsg = shopKind; }
            if (HubRepStage() >= 2) { shopMsg = shopUneasy; shopMood = -1; }
            PlaySound("Assets/Audio/Menu Select.audio.json", 0.6, 1, false);
        } else {
            HostClose();
        }
    }
    return;
}

// 2: Chanterelle's shop. Rows: 0-4 the old goods, 5 Sword, 6 Shield, 7 Bow, 8 Arrows x10, 9 Propeller Flower, 10 Leave.
// Left column is rows 0-5, right column 6-10. Prices are the base price times the Reputation Kit multiplier.
if (mode == 2) {
    var moved = 0;
    if (KeyPressed("Up") || KeyPressed("W") || GamepadPressed("Up") || stickNew == 1) { shopSel = (shopSel + 10) % 11; moved = 1; }
    if (KeyPressed("Down") || KeyPressed("S") || GamepadPressed("Down") || stickNew == 2) { shopSel = (shopSel + 1) % 11; moved = 1; }
    if (KeyPressed("Left") || KeyPressed("A") || GamepadPressed("Left") || stickNew == 3) {
        if (shopSel >= 6) { shopSel = shopSel - 6; moved = 1; }
    }
    if (KeyPressed("Right") || KeyPressed("D") || GamepadPressed("Right") || stickNew == 4) {
        if (shopSel < 6) { shopSel = Min(10, shopSel + 6); moved = 1; }
    }
    if (moved == 1) {
        shopMsg = ""; shopMood = 0;
        PlaySound("Assets/Audio/Menu Move.audio.json", 0.55, 1, false);
    }
    if (HubBackPressed() && inputDelay <= 0) {
        HostClose();
        PlaySound("Assets/Audio/Menu Back.audio.json", 0.5, 1, false);
        return;
    }
    if (advance) {
        if (shopSel == 10) {
            HostClose();
            PlaySound("Assets/Audio/Menu Back.audio.json", 0.5, 1, false);
            return;
        }
        var price = HubShopPrice(itemPrice[shopSel]);
        var coins = GlobalGet("coins");
        var hearts = GlobalGet("hearts"); var maxHearts = GlobalGet("maxHearts");
        var lives = GlobalGet("lives"); var power = GlobalGet("power");
        var heldNow = GlobalGet("heldItem"); var arrowsNow = GlobalGet("arrows");
        var heldWant = 0;
        if (shopSel == 5) { heldWant = 1; }
        if (shopSel == 6) { heldWant = 2; }
        if (shopSel == 7) { heldWant = 3; }
        if (shopSel == 9) { heldWant = 4; }
        var deny = "";
        if (HubRepStage() >= 3 && (shopSel == 5 || shopSel == 7 || shopSel == 8)) { deny = shopRefuse; }
        else if (coins < price) { deny = shopPoor; }
        else if (shopSel == 0 && maxHearts >= 6) { deny = shopFull; }
        else if (shopSel >= 2 && shopSel <= 4 && power == shopSel - 1) { deny = shopHave; }
        else if (heldWant > 0 && heldNow == heldWant) { deny = shopHave; }
        else if (shopSel == 8 && arrowsNow >= 99) { deny = shopQuiver; }
        if (!StringEquals(deny, "")) {
            shopMsg = deny; shopMood = -1;
            PlaySound("Assets/Audio/Shop Deny.audio.json", 0.8, 1, false);
        } else {
            GlobalSet("coins", coins - price);
            if (shopSel == 0) {
                GlobalSet("maxHearts", maxHearts + 1);
                GlobalSet("hearts", Min(maxHearts + 1, hearts + 1));
            }
            if (shopSel == 1) { GlobalSet("lives", Min(99, lives + 1)); }
            if (shopSel >= 2 && shopSel <= 4) { GlobalSet("power", shopSel - 1); }
            // A held item replaces whatever Pip carried (the Item Kit carries heldItem / arrows between rooms).
            if (heldWant > 0) {
                // The Item Kit tosses the item Pip carried out beside him instead of losing it.
                if (ResourceExists("Item Kit")) { ItemGrantGlobal(heldWant); } else { GlobalSet("heldItem", heldWant); }
            }
            if (shopSel == 7) { GlobalSet("arrows", Min(99, arrowsNow + 12)); }
            if (shopSel == 8) { GlobalSet("arrows", Min(99, arrowsNow + 10)); }
            // The Explorer takes the new counters over (it mirrors them into the globals every step).
            RoomValueSet("heroReload", 1);
            shopMsg = itemBought[shopSel]; shopMood = 1;
            PlaySound("Assets/Audio/Shop Buy.audio.json", 0.85, 1, false);
            var seller = InstanceFind("NPC Chanterelle", 0);
            if (seller > 0) {
                ParticleBurstAt("Assets/Particles/Shop Sparkle.particle.json", InstanceGetX(seller), InstanceGetY(seller) - 30, 0, 1, 0);
            }
            AchOnShop();
            HubSave(0);
        }
    }
}
"""

HOST_DRAWGUI = """// Arrival card, the dialogue box (portrait, name plate, typewriter text), the shop and the fade.
var sw = WindowGetWidth(); var sh = WindowGetHeight();
var sx = sw / 1280; var sy = sh / 720; var s = Min(sx, sy);
var box = "Assets/User Interfaces/Dialogue Box.ui.json";
var shop = "Assets/User Interfaces/Shop Menu.ui.json";

if (t < 3.4) {
    var a = 1;
    if (t < 0.6) { a = t / 0.6; }
    if (t > 2.6) { a = (3.4 - t) / 0.8; }
    DrawSetAlpha(a * 0.85); DrawSetColorRgb(29, 48, 67);
    DrawRoundRectEx(430 * sx, 128 * sy, 850 * sx, 222 * sy, 12 * s, 0);
    DrawSetAlpha(a); DrawSetColorRgb(255, 210, 63);
    DrawRoundRectEx(430 * sx, 128 * sy, 850 * sx, 222 * sy, 12 * s, 3 * s);
    DrawSetTextAlign("center");
    HubShadowText(640 * sx, 138 * sy, "HEARTHOLLOW", 38 * s, 255, 235, 185);
    HubShadowText(640 * sx, 186 * sy, "the village under the Great Lantern Tree", 18 * s, 220, 239, 224);
    DrawSetTextAlign("left");
    DrawSetAlpha(1);
}

if (mode == 1 || mode == 2) {
    var who = speaker;
    if (mode == 2) { who = 2; }
    // Pip's portrait follows the taint stage (Portrait Pip Spore 1..3).
    var pipStage = HubRepStage();
    for (var i = 0; i < 6; i += 1) {
        var showIt = 0;
        if (i == who) { showIt = 1; }
        if (i == 0 && pipStage > 0) { showIt = 0; }
        UiSetVisible(box, portraitId[i], showIt == 1);
    }
    for (var j = 1; j <= 3; j += 1) { UiSetVisible(box, portraitId[5 + j], who == 0 && pipStage == j); }
    UiSetText(box, "nameText", speakerName[who]);
    DrawUi(box);
    DrawSetFont("Consolas"); DrawSetAlpha(1);
    var body = pageText;
    var shown = Floor(reveal);
    if (mode == 2) {
        body = shopMsg;
        if (StringEquals(body, "")) { body = itemInfo[shopSel]; }
        shown = StringLength(body);
    }
    var lines = StringSplitCount(body, "/");
    var offset = 0;
    for (var ln = 1; ln <= lines; ln += 1) {
        var line = StringSplitPart(body, "/", ln);
        var n = Clamp(shown - offset, 0, StringLength(line));
        if (n > 0) {
            var cr = 248; var cg = 252; var cb = 244;
            if (mode == 2 && shopMood < 0) { cr = 255; cg = 196; cb = 170; }
            if (mode == 2 && shopMood > 0) { cr = 255; cg = 236; cb = 160; }
            HubShadowText(318 * sx, (474 + (ln - 1) * 44) * sy, StringCopy(line, 1, n), 25 * s, cr, cg, cb);
        }
        offset += StringLength(line) + 1;
    }
    if (mode == 1 && reveal >= totalChars) {
        var bob = Abs(Sin(t * 240)) * 5 * s;
        DrawSetTextAlign("right");
        HubShadowText(1160 * sx, 604 * sy + bob, "v", 24 * s, 255, 210, 63);
        HubShadowText(1130 * sx, 610 * sy, StringJoin3(StringOf(page), " / ", StringOf(pageCount)), 18 * s, 200, 214, 200);
        DrawSetTextAlign("left");
    }
}

if (mode == 2) {
    UiSetText(shop, "purseText", StringOf(GlobalGet("coins")));
    DrawUi(shop);
    var coinsNow = GlobalGet("coins");
    var pct = HubShopPercent();
    if (pct < 0) { HubShadowText(166 * sx, 110 * sy, StringJoin3("FRIENDLY DISCOUNT ", StringOf(pct), "%"), 18 * s, 170, 240, 150); }
    if (pct > 0) { HubShadowText(166 * sx, 110 * sy, StringJoin3("SURCHARGE +", StringOf(pct), "%"), 18 * s, 255, 160, 140); }
    var repShop = HubRepStage();
    for (var r = 0; r < 11; r += 1) {
        var rx = shopX[r]; var ry = shopY[r];
        var refused = 0;
        if (repShop >= 3 && (r == 5 || r == 7 || r == 8)) { refused = 1; }
        var shownPrice = HubShopPrice(itemPrice[r]);
        if (r == shopSel) {
            var pulse = 0.75 + 0.25 * Sin(t * 360);
            DrawSetAlpha(0.35); DrawSetColorRgb(255, 210, 63);
            DrawRoundRectEx(rx * sx, ry * sy, (rx + 480) * sx, (ry + 40) * sy, 6 * s, 0);
            DrawSetAlpha(pulse);
            DrawRoundRectEx(rx * sx, ry * sy, (rx + 480) * sx, (ry + 40) * sy, 6 * s, 3 * s);
            DrawSetAlpha(1);
        }
        var nr = 248; var ng = 252; var nb = 244;
        if (r < 10 && (coinsNow < shownPrice || refused == 1)) { nr = 170; ng = 160; nb = 170; }
        HubShadowText((rx + 56) * sx, (ry + 7) * sy, itemName[r], 22 * s, nr, ng, nb);
        if (r < 10) {
            var pr = 255; var pg = 223; var pb = 145;
            if (shownPrice < itemPrice[r]) { pr = 170; pg = 240; pb = 150; }
            if (shownPrice > itemPrice[r]) { pr = 255; pg = 170; pb = 140; }
            DrawSetTextAlign("right");
            HubShadowText((rx + 436) * sx, (ry + 7) * sy, StringOf(shownPrice), 22 * s, pr, pg, pb);
            DrawSetTextAlign("left");
        }
    }
}

if (leaving == 1) {
    DrawSetAlpha(Clamp(fadeT / 0.5, 0, 1)); DrawSetColorRgb(16, 12, 24);
    DrawRectangle(0, 0, sw, sh);
}
DrawSetAlpha(1); DrawSetColorRgb(255, 255, 255);
"""

TREE_CREATE = """// Lantern Tree Glow: the Great Lantern Tree's returned Glowspores, drawn over the Village Far
// backdrop. It shows "Lantern Tree Lights N" for N Glowspores (all five after the ending), and moves
// with Village Far's parallax (0.2) so the lights stay in the tree.
count = HubGlowCount();
if (GlobalGet("gameComplete") == 1) { count = 5; }
farParallax = 0.2; t = 0;
SpriteSetDepth(9300);
if (count <= 0) { SpriteSetVisible(false); }
if (count == 1) { SpriteSet("Assets/Sprites/Lantern Tree Lights 1.image.json"); }
if (count == 2) { SpriteSet("Assets/Sprites/Lantern Tree Lights 2.image.json"); }
if (count == 3) { SpriteSet("Assets/Sprites/Lantern Tree Lights 3.image.json"); }
if (count == 4) { SpriteSet("Assets/Sprites/Lantern Tree Lights 4.image.json"); }
if (count >= 5) { SpriteSet("Assets/Sprites/Lantern Tree Lights 5.image.json"); }
"""

TREE_STEP_END = """var dt = Clamp(DeltaTime, 0, 0.05);
t += dt;
// A background with parallax p is drawn at its room position + view left * (1 - p).
x = ViewGetPosX(0) * (1 - farParallax);
y = 0;
image_alpha = 0.84 + 0.16 * Sin(t * 75);
"""

SMOKE_PUFFS = 7       # puffs looping over each chimney
SMOKE_DEPTH = 8450     # just in front of Village Houses (8500), behind the tiles and actors
HOUSES_PARALLAX = 0.6  # Village Houses' parallaxX (worlds_art.py writes it)
HOUSES_STRIP = 1024    # Village Houses' width: the strip repeats every 1024 px

SMOKE_CREATE = """// Chimney Smoke: one puff of live smoke over the Village Houses backdrop, whose chimneys are
// painted without smoke. The puff belongs to the chimney nearest to where the room placed it and
// takes that chimney's next free slot, so the @PUFFS@ puffs of a chimney share its cycle evenly. Each
// cycle it leaves the chimney top small and faint, swells, rises, leans with a slow breeze shared
// by the whole village, and fades out. Village Houses scrolls with parallax @PARALLAX@ and repeats every
// @STRIP@ px, so each step the puff follows the copy of its chimney nearest the middle of the view.
chimX = @CHIMX@;
chimY = @CHIMY@;
puffs = @PUFFS@; housesParallax = @PARALLAX@; stripW = @STRIP@;
chimney = 0;
var best = 999999;
for (var k = 0; k < PgListSize(chimX); k += 1) {
    var d = Abs(x - chimX[k]) + Abs(y - chimY[k]);
    if (d < best) { best = d; chimney = k; }
}
homeX = chimX[chimney]; homeY = chimY[chimney];
slot = RoomValueGet("smokeSlot" + chimney, 0);
RoomValueSet("smokeSlot" + chimney, slot + 1);
slot = slot % puffs;
// Each chimney breathes at its own pace (3.9, 4.25 or 4.6 s a puff).
life = 3.9 + 0.35 * (chimney - Floor(chimney / 3) * 3);
age = life * slot / puffs + 0.37 * chimney;
while (age >= life) { age -= life; }
mirror = 1;
if (slot % 2 == 1) { mirror = -1; }
clock = 0;
SpriteSetDepth(@DEPTH@);
image_alpha = 0; image_xscale = 0.35; image_yscale = 0.35;
"""

SMOKE_STEP_END = """var dt = Clamp(DeltaTime, 0, 0.05);
clock += dt;
age += dt;
while (age >= life) { age -= life; }
var f = age / life;
// The breeze: every chimney's smoke leans right, now more, now less (a 17 s swell).
var breeze = 18 + 8 * Sin(clock * 21);
var rise = 54 * (0.5 * f + 0.5 * (1 - (1 - f) * (1 - f)));
var lean = breeze * f * f + Sin(clock * 95 + slot * 77 + chimney * 41) * 2.5 * f;
var s = 0.35 + 1.05 * Sqrt(f);
image_alpha = 0.5 * Clamp(f / 0.06, 0, 1) * Clamp((1 - f) / 0.7, 0, 1);
image_xscale = mirror * s * (1 + 0.06 * Sin(clock * 140 + slot * 50));
image_yscale = s;
// A background with parallax p is drawn at its room position + view left * (1 - p), repeated.
var viewLeft = ViewGetPosX(0);
var shift = viewLeft * (1 - housesParallax);
var copy = Round((viewLeft + ViewGetBoundsW(0) * 0.5 - shift - homeX) / stripW);
x = homeX + shift + copy * stripW + lean;
y = homeY - 14 - rise;
"""


def chimney_tops():
    """Village Houses' chimneys (centre x, base row y) from worlds_art.py, the painter of the strip."""
    import sys
    here = os.path.dirname(os.path.abspath(__file__))
    if here not in sys.path:
        sys.path.insert(0, here)
    import worlds_art
    return [(round(x, 1), round(y, 1)) for (x, y) in worlds_art.Village.chimneys()]


def build_smoke_puff():
    """Smoke Puff: a soft 24x20 puff (three tones, half-clear rim) for Chimney Smoke to scale and fade."""
    import sys
    here = os.path.dirname(os.path.abspath(__file__))
    if here not in sys.path:
        sys.path.insert(0, here)
    from PIL import Image
    import world_art
    W, H = 24, 20
    blobs = [(12, 12, 7.5), (6.5, 13.5, 5), (17.5, 13.5, 5), (9.5, 7.5, 5), (15, 8, 4.5)]
    hi, mid, lo = (242, 234, 244), (216, 206, 226), (184, 172, 202)

    def inside(x, y):
        return any((x + 0.5 - bx) ** 2 + (y + 0.5 - by) ** 2 <= r * r for (bx, by, r) in blobs)

    img = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    px = img.load()
    for y in range(H):
        for x in range(W):
            if not inside(x, y):
                continue
            # light from the top left, on the blob this pixel is deepest inside
            bx, by, r = max(blobs, key=lambda b: b[2] - ((x + 0.5 - b[0]) ** 2 + (y + 0.5 - b[1]) ** 2) ** 0.5)
            k = (-(x + 0.5 - bx) * 0.55 - (y + 0.5 - by) * 0.85) / r
            if y >= 16:
                k -= 0.4
            if k > 0.3 or (k > 0.18 and (x + y) % 2 == 0):
                c = hi
            elif k < -0.4 or (k < -0.28 and (x + y) % 2 == 0):
                c = lo
            else:
                c = mid
            rim = not all(inside(x + dx, y + dy) for (dx, dy) in ((1, 0), (-1, 0), (0, 1), (0, -1)))
            px[x, y] = c + ((150 if rim else 255),)
    world_art.write_image("Smoke Puff", img, "sprite", (12, 12), template_name="Cloud Hills", tag_name="Still")


STALL_CREATE = """// Shop Stall: Chanterelle's market stall (decoration; the shop itself is run by Village Host).
SpriteSetDepth(150);
"""

EXIT_CREATE = """// Exit Sign: the road out of Hearthollow. Up / action next to it, or walking off the right edge,
// asks Village Host to fade back to the Overworld.
near = 0; t = 0; asked = 0;
SpriteSetDepth(120);
"""

EXIT_STEP = """var dt = Clamp(DeltaTime, 0, 0.05);
t += dt;
near = 0;
var hero = RoomValueGet("heroId", 0);
if (hero > 0) {
    var hx = InstanceGetX(hero); var hy = InstanceGetY(hero);
    if (Abs(hx - x) <= 30 && Abs(hy - y) <= 40) { near = 1; }
    var go = 0;
    if (hx >= RoomGetWidth() - 20) { go = 1; }
    if (near == 1) {
        if (KeyPressed("Up") || KeyPressed("W") || GamepadPressed("Up") || HubActionPressed()) { go = 1; }
        if (RoomValueGet("talk", 0) == 1) { go = 1; }
    }
    if (go == 1 && asked == 0 && RoomValueGet("dialogue", 0) == 0) {
        asked = 1;
        RoomValueSet("leaveRequest", 1);
    }
}
"""

EXIT_DRAWGUI = """if (near == 1 && asked == 0 && RoomValueGet("dialogue", 0) == 0) {
    var s = Min(WindowGetWidth() / 1280, WindowGetHeight() / 720);
    var bob = Abs(Sin(t * 180)) * 4 * s;
    HubPromptBubble(HubGuiX(x), HubGuiY(y - 40) - bob, "UP", "To the map", s);
}
"""


def object_json(sprite, script_class, events):
    return {"schemaVersion": 2, "dimension": "TwoD", "sprite": sprite, "model": "",
            "components": [{"type": "ScriptComponent", "enabled": True, "props": {"ScriptClass": script_class}}],
            "events": events}


def write_object(name, sprite, events):
    rel = "Objects/%s.object.json" % name
    sprite_path = ("Assets/Sprites/%s.image.json" % sprite) if sprite else ""
    write_json(rel, object_json(sprite_path, name, list(events.keys())))
    write_meta(rel, "GameObject", name)
    for event, code in events.items():
        write_text("Objects/%s/%s.pgsl" % (name, event), code)


def build_scripts_and_objects():
    write_text("Scripts/Hub Common.pgsl", HUB_COMMON)
    write_meta("Scripts/Hub Common.pgsl", "Script", "Hub Common")
    build_lines_script()

    nx = pgsl_list([p[0] for p in NODES])
    ny = pgsl_list([p[1] for p in NODES])
    trails = trace_map_paths()
    marker_create = (MARKER_CREATE.replace("@NODEX@", nx).replace("@NODEY@", ny)
                     .replace("@NAMES@", pgsl_list(NODE_NAMES)).replace("@INFO@", pgsl_list(NODE_INFO))
                     .replace("@PATHX@", pgsl_list([t[0] for t in trails]))
                     .replace("@PATHY@", pgsl_list([t[1] for t in trails]))
                     .replace("@PATHC@", pgsl_list([t[2] for t in trails])))
    write_object("Map Marker", "Pip Map", {"Create": marker_create, "Step": MARKER_STEP, "DrawGui": MARKER_DRAWGUI})
    write_object("Map Node", "Map Node Sprite", {"Create": NODE_CREATE.replace("@NODEX@", nx).replace("@NODEY@", ny),
                                          "Step": NODE_STEP})
    write_object("Map Clouds", "Overworld Clouds", {"Create": CLOUDS_CREATE, "Step": CLOUDS_STEP})

    for obj, sprite, key, head, wander in NPCS:
        create = (NPC_CREATE.replace("@OBJ@", obj).replace("@KEY@", str(key)).replace("@NAME@", sprite)
                  .replace("@HEAD@", str(head)).replace("@WANDER@", str(wander)).replace("@SPRITE@", sprite + " Sprite"))
        write_object(obj, sprite + " Sprite", {"Create": create, "Step": NPC_STEP, "DrawGui": NPC_DRAWGUI})

    host_create = (HOST_CREATE.replace("@INFO@", pgsl_list([wrapped(s) for s in SHOP_INFO]))
                   .replace("@BOUGHT@", pgsl_list([wrapped(s) for s in SHOP_BOUGHT]))
                   .replace("@WELCOME@", wrapped(SHOP_WELCOME)).replace("@POOR@", wrapped(SHOP_POOR))
                   .replace("@FULL@", wrapped(SHOP_FULL)).replace("@HAVE@", wrapped(SHOP_HAVE))
                   .replace("@REFUSE@", wrapped(SHOP_REFUSE)).replace("@QUIVER@", wrapped(SHOP_QUIVER))
                   .replace("@KIND@", wrapped(SHOP_KIND)).replace("@UNEASY@", wrapped(SHOP_UNEASY))
                   .replace("@SHOPX@", pgsl_list([float(shop_row_pos(i)[0]) for i in range(11)]))
                   .replace("@SHOPY@", pgsl_list([float(shop_row_pos(i)[1]) for i in range(11)])))
    write_object("Village Host", "", {"Create": host_create, "Step": HOST_STEP, "DrawGui": HOST_DRAWGUI})
    write_object("Lantern Tree Glow", "Lantern Tree Lights 1", {"Create": TREE_CREATE, "StepEnd": TREE_STEP_END})
    tops = chimney_tops()
    smoke_create = (SMOKE_CREATE.replace("@CHIMX@", pgsl_list([float(p[0]) for p in tops]))
                    .replace("@CHIMY@", pgsl_list([float(p[1]) for p in tops]))
                    .replace("@PUFFS@", str(SMOKE_PUFFS)).replace("@PARALLAX@", str(HOUSES_PARALLAX))
                    .replace("@STRIP@", str(HOUSES_STRIP)).replace("@DEPTH@", str(SMOKE_DEPTH)))
    write_object("Chimney Smoke", "Smoke Puff", {"Create": smoke_create, "StepEnd": SMOKE_STEP_END})
    write_object("Shop Stall", "Shop Stall Sprite", {"Create": STALL_CREATE})
    write_object("Exit Sign", "Exit Sign Sprite", {"Create": EXIT_CREATE, "Step": EXIT_STEP, "DrawGui": EXIT_DRAWGUI})


# ====================================================================== rooms
def rid(text):
    return hashlib.md5(("mushroom-meadow-hub-room:" + text).encode("utf-8")).hexdigest()


def room_shell(room_id, name, width, follow, bg_colour, nodes, fit_view=False):
    # fit_view: the room's Level Rules fits the world to the window (RulesFitView), so the room must not letterbox itself.
    viewport = {"enabled": True, "sourceX": 0, "sourceY": 0, "sourceWidth": 640, "sourceHeight": 360,
                "portX": 0, "portY": 0, "portWidth": 1280, "portHeight": 720, "followTarget": follow,
                "followMarginX": 220 if follow else 0, "followMarginY": 64 if follow else 0,
                "followSpeedX": 20 if follow else -1, "followSpeedY": -1}
    return {
        "$schema": "genesis.room", "version": 1, "id": room_id, "name": name, "dimension": "TwoD",
        "settings": {"width": width, "height": 360, "depth": 720, "windowWidth": 1280, "windowHeight": 720,
                     "scaleViewportsWithWindow": not fit_view, "targetFps": 60, "fixedFps": 60, "vsync": True,
                     "captureMouse": False, "gridSize": 32, "snapEnabled": True, "pixelArtSampling": True},
        "environment": {"backgroundColor": bg_colour, "fogEnabled": False, "postEffects": ["SNES Look"]},
        "viewports": [viewport],
        "layers": [{"id": "scenery", "name": "Backdrop", "order": 0, "enabled": True},
                   {"id": "tiles", "name": "Painted world", "order": 1, "enabled": True},
                   {"id": "actors", "name": "Actors", "order": 2, "enabled": True}],
        "nodes": nodes,
    }


def background(nodes, key, name, asset, layout, depth, repeat, x=0, y=0):
    nodes.append({"id": rid(key), "kind": "Background", "name": name, "enabled": True, "layerId": "scenery",
                  "transform": {"position": [x, y, 0], "rotation": [0, 0, 0], "scale": [1, 1, 1]},
                  "background": {"asset": "Assets/Sprites/%s.image.json" % asset, "mode": "TwoD", "layout": layout,
                                 "depth": depth, "repeatX": repeat, "opacity": 1}})


def instance(nodes, key, name, prefab, x, y, order=0):
    nodes.append({"id": rid(key), "kind": "GameObject", "name": name, "enabled": True, "layerId": "actors",
                  "order": order, "transform": {"position": [x, y, 0], "rotation": [0, 0, 0], "scale": [1, 1, 1]},
                  "gameObject": {"prefab": "Assets/Objects/%s.object.json" % prefab}})


def write_room(name, room):
    rel = "Rooms/%s.room.json" % name
    write_json(rel, room)
    write_meta(rel, "Room", name)


def build_overworld():
    nodes = []
    background(nodes, "ow-map", "Overworld map", "Overworld Map", "Single", 10000, False)
    instance(nodes, "ow-clouds-a", "Clouds A", "Map Clouds", 0, 0)
    instance(nodes, "ow-clouds-b", "Clouds B", "Map Clouds", 1024, 0)
    for i, (x, y) in enumerate(NODES):
        instance(nodes, "ow-node-%d" % i, "Node %d %s" % (i, NODE_NAMES[i]), "Map Node", x, y)
    instance(nodes, "ow-marker", "Pip", "Map Marker", NODES[0][0], NODES[0][1] + 2, -100)
    write_room("Overworld", room_shell(rid("overworld"), "Overworld", 640, "", [0.18, 0.3, 0.22, 1], nodes))


# Hearthollow: rows 0..11 (32 px), ground surface row 9 (y = 288). Same legend as level_gen.py for
# terrain and decor; actors: @ Explorer, 1 Elder Morel, 2 Chanterelle, 3 Bram, 4 Puffball, 5 Old Wick,
# $ Shop Stall, X Exit Sign.
VILLAGE = [
    ("Village gate: Pip arrives by the welcome sign; Bram's mail round", [
        "", "", "", "", "", "", "", "",
        ".l.@.s.f.c..l3..",
        "GGGGGGGGGGGGGGGG", "GGGGGGGGGGGGGGGG", "GGGGGGGGGGGGGGGG",
    ]),
    ("Market: Chanterelle's stall", [
        "", "", "", "", "", "", "", "",
        ".n.f...$.2...f.n..",
        "GGGGGGGGGGGGGGGGGG", "GGGGGGGGGGGGGGGGGG", "GGGGGGGGGGGGGGGGGG",
    ]),
    ("Elder's green: a raised plaza facing the Great Lantern Tree", [
        "", "", "", "", "", "", "",
        "....l.f..1..F.l...",
        ".t.GGGGGGGGGGGG.c.",
        "GGGGGGGGGGGGGGGGGG", "GGGGGGGGGGGGGGGGGG", "GGGGGGGGGGGGGGGGGG",
    ]),
    # The caps are 3 tiles (96 px) up: too close to Pip's 110 px jump, so a 1-tile mossy mound in
    # front of them makes the climb two easy hops (32 px, then 64 px). The mound stops a tile short
    # of the cap: under the cap there is only room to walk on the ground (reach_hub.py checks it).
    ("Mushroom caps: Puffball's lookout (up the mound) and a log perch", [
        "", "", "", "", "",
        "....4...........",
        "...MMMM....LLL..",
        "f...||..........",
        "GG..||.m..b..t..",
        "GGGGGGGGGGGGGGGG", "GGGGGGGGGGGGGGGG", "GGGGGGGGGGGGGGGG",
    ]),
    ("Lamplighter's row and the road out", [
        "", "", "", "", "", "", "", "",
        ".l..5..l..nX.l",
        "GGGGGGGGGGGGGG", "GGGGGGGGGGGGGG", "GGGGGGGGGGGGGG",
    ]),
]
ROWS = 12
DECOR = {"t": 16, "f": 17, "m": 18, "b": 19, "n": 20, "s": 21, "v": 22, "r": 23, "|": 29,
         "l": 35, "F": 36, "c": 37}
ACTORS = {"@": ("Explorer", "Explorer"), "1": ("Elder Morel", "NPC Elder Morel"), "2": ("Chanterelle", "NPC Chanterelle"),
          "3": ("Bram", "NPC Bram"), "4": ("Puffball", "NPC Puffball"), "5": ("Old Wick", "NPC Old Wick"),
          "$": ("Chanterelle's stall", "Shop Stall"), "X": ("Exit sign", "Exit Sign")}
SOLID = set(range(0, 16)) | set(range(24, 29)) | {33, 34, 38}


def build_hearthollow():
    grid = [[] for _ in range(ROWS)]
    for name, rows in VILLAGE:
        assert len(rows) == ROWS, name
        width = max(len(r) for r in rows)
        for i in range(ROWS):
            grid[i].extend(rows[i].ljust(width, "."))
    W = len(grid[0])
    at = lambda r, c: grid[r][c] if 0 <= r < ROWS and 0 <= c < W else "."
    layers = {"World": [], "Decor": []}
    solid = set()

    def put(layer, c, r, idx):
        layers[layer].append({"x": c, "y": r, "tileX": idx % 8, "tileY": idx // 8})
        if layer == "World" and idx in SOLID:
            solid.add((c, r))

    found = {}
    for c in range(W):
        for r in range(ROWS):
            ch = grid[r][c]
            if ch == "G":
                up, left, right, down = at(r - 1, c) == "G", at(r, c - 1) == "G", at(r, c + 1) == "G", at(r + 1, c) == "G"
                if r == ROWS - 1:
                    down = True
                if c == 0:
                    left = True
                if c == W - 1:
                    right = True
                if not up:
                    idx = 0 if (left and right) or (not left and not right) else (5 if not left else 6)
                elif not left:
                    idx = 24
                elif not right:
                    idx = 25
                elif not down:
                    idx = 38
                else:
                    idx = 15 if (c * 7 + r * 13) % 11 == 0 else 1
                put("World", c, r, idx)
            elif ch == "L":
                left, right = at(r, c - 1) == "L", at(r, c + 1) == "L"
                put("World", c, r, 13 if left and right else (12 if not left else 14))
            elif ch == "M":
                left, right = at(r, c - 1) == "M", at(r, c + 1) == "M"
                put("World", c, r, 27 if left and right else (26 if not left else 28))
            elif ch in DECOR:
                put("Decor", c, r, DECOR[ch])
            elif ch in ACTORS:
                found.setdefault(ch, []).append((c, r))
            elif ch != ".":
                raise SystemExit("Unknown map character %r at column %d row %d" % (ch, c, r))
    for ch in ACTORS:
        assert len(found.get(ch, [])) == 1, "exactly one %r" % ch

    nodes = []
    background(nodes, "hh-sky", "Village sky", "Village Sky", "StretchView", 10000, False)
    background(nodes, "hh-far", "Village far hills and the Great Lantern Tree", "Village Far", "Single", 9500, True)
    background(nodes, "hh-houses", "Village houses", "Village Houses", "Single", 8500, True)
    for layer, depth, collision in (("World", 500, True), ("Decor", 700, False)):
        cells = sorted(layers[layer], key=lambda cell: (cell["x"], cell["y"]))
        nodes.append({"id": rid("hh-" + layer), "kind": "TileLayer", "name": layer, "enabled": True,
                      "layerId": "tiles",
                      "transform": {"position": [0, 0, 0], "rotation": [0, 0, 0], "scale": [1, 1, 1]},
                      "tileLayer": {"tileset": "Assets/Sprites/Village Tiles.image.json", "cellWidth": 32,
                                    "cellHeight": 32, "collisionEnabled": collision, "depth": depth, "cells": cells}})
    # Room-wide controllers first so their Create runs before the villagers'.
    instance(nodes, "hh-rules", "Level Rules", "Level Rules", 16, 16, -300)
    instance(nodes, "hh-host", "Village Host", "Village Host", 48, 16, -290)
    instance(nodes, "hh-tree", "Lantern Tree Glow", "Lantern Tree Glow", 0, 0, -280)
    # Live chimney smoke: SMOKE_PUFFS puffs per Village Houses chimney, placed on its chimney top
    # (where the strip draws it while the view is at the room's left edge).
    for i, (chx, chy) in enumerate(chimney_tops()):
        for k in range(SMOKE_PUFFS):
            instance(nodes, "hh-smoke-%d-%d" % (i, k), "Chimney Smoke %d.%d" % (i + 1, k + 1), "Chimney Smoke",
                     chx, chy - 16, -270)
    for ch, (label, prefab) in ACTORS.items():
        c, r = found[ch][0]
        x, y = c * 32 + 16, (r + 1) * 32
        assert (c, r) not in solid and (c, r - 1) not in solid, label
        assert (c, r + 1) in solid, "%s must stand on solid ground (column %d row %d)" % (label, c, r)
        instance(nodes, "hh-" + prefab, label, prefab, x, y, -100 if ch == "@" else 0)
    room = room_shell(rid("hearthollow"), "Hearthollow", W * 32, "Explorer", [0.98, 0.74, 0.52, 1], nodes, fit_view=True)
    write_room("Hearthollow", room)
    print("Hearthollow %dx360 (%d columns), solid cells %d" % (W * 32, W, len(solid)))
    for ch, (label, _) in ACTORS.items():
        c, r = found[ch][0]
        print("  %-16s x=%d y=%d" % (label, c * 32 + 16, (r + 1) * 32))


def main():
    build_smoke_puff()
    build_particles()
    build_ui()
    build_scripts_and_objects()
    build_overworld()
    build_hearthollow()
    print("HUB content written to", ASSETS)


if __name__ == "__main__":
    main()
