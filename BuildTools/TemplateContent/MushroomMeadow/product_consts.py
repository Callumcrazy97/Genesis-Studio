"""Mushroom Meadow product constants shared by frontend_gen.py, hero_gen.py, options_kit_gen.py and product_gen.py.

One place for the version string (also written to the project file by product_gen.py), the save fields the
finished-product features add, and the lists those features count (enemy kinds, items, achievements).
Plain data only: importing this module writes nothing.
"""

VERSION = "1.0.0"
GAME_NAME = "Mushroom Meadow"

# Enemy kinds counted on the Collection page (index = the number AchKindOf returns; 8 and 9 are the bosses).
FOES = ["Acorn Walker", "Buzz Bee", "Flit Bat", "Glow Grub", "Dusk Moth", "Burr Hopper", "Bloatfin",
        "Driftjelly", "Grumblecrag", "Duskmantle"]

# Items counted as "found" (bit index in the itemsSeen global): five power-ups, then the four held items.
ITEMS = ["Glide Seed", "Spore Puff", "Bark Shield", "Starlit Firefly", "Heart Berry",
         "Sword", "Shield", "Bow", "Propeller Flower"]

# Fields added to every save slot by the finished-product features, appended after the earlier fields so an
# older save (which ends before them) still loads and starts these at their defaults.
PRODUCT_SAVE_FIELDS = (["difficulty", "playTime", "deaths", "statCoins", "itemsSeen", "shopBuys"]
                       + ["bestTime%d" % i for i in range(1, 6)]
                       + ["foe%d" % i for i in range(len(FOES))])

# Difficulty is also remembered in the Options slot as the default offered by New Game.
PRODUCT_OPTION_FIELDS = ["difficulty"]

DIFFICULTIES = ["STORY", "NORMAL", "CHALLENGE"]
