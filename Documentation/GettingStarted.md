# Getting started with Genesis Studio

Genesis makes 2D and 3D games. Everything you make is a **resource** in the Assets panel on the
left: images, Objects, Rooms, models, sounds and so on. Every editor works the same way: a row of
numbered steps under its toolbar takes you from start to **Use in game**.

## Your first game in four steps

The Home page shows these four steps and ticks them off as you go.

1. **Draw a sprite.** On Home, choose *Draw a sprite* and give it a name. Draw with the tools on the left. The steps along the top are Draw, Animate, Rig and Use in game.
2. **Make an Object.** Choose *Make an Object*. On the *Look* step pick your sprite. On the *Behaviour* step choose a recipe such as **Move with arrow keys** or **Platformer controls**: it writes the code for you, and you can read and change it afterwards.
3. **Build a Room.** Choose *Build a Room*. On the *Place* step pick your Object on the left and click in the Room to place it.
4. **Press Run.** The blue Run button at the top right plays your game. F5 does the same from anywhere.

## Recipes you can combine

A recipe adds ordinary code to an Object's events, marked with a comment so you can find it.

- **Move with arrow keys** — walk in every direction; solid tiles block the way.
- **Platformer controls** — run, jump and land on solid tiles.
- **Patrol left and right** — walk back and forth around where it was placed.
- **Chase the player** — follow the nearest Object named Player.
- **Projectile** — fly in a straight line and disappear on leaving the Room.
- **Collectible** — float, and disappear when the Player touches it.
- **Spin**, **Float up and down**, **Wrap around the screen**, **Disappear after 3 seconds**.

Open them any time from an Object's *Options › Behaviour recipes…*.

## Making a 3D scene

1. Create a **Terrain**. On the *Shape* step choose a landform card such as *Rolling hills*; then *Sculpt* and *Paint* it.
2. Choose **Use in game** to make a 3D Room that contains the terrain.
3. In the Room, the *Sky* step has scene presets: *Sunny day*, *Golden hour*, *Night* and more. One click sets the sky, time and weather.
4. Create a **Model**. In the Model editor, *Start* offers shapes and *Generate › Tree… / Rock…*. Choose **Use in game** to make an Object from it, then place it on the Room's *Place* step.

## Where things are

- **Assets** (left) — every resource. *＋ New* creates one; double-click opens it.
- **Inspector** (right edge) — properties of whatever is selected.
- **Console** (bottom edge) — messages, and the problems Validate finds.
- **Run, Debug, Validate, Save project** — top right of the window.
- **Options** in each editor — everything that is not part of the main steps.
- **Help › Commands…** — every game-code command, with examples.

## If something goes wrong

- **Validate** lists problems in the Console; double-click one to open the resource.
- Every change can be undone with Ctrl+Z, including recipes, landforms and scene presets.
- This guide is always at *Help › Getting started* (F1).
- *Help › Genesis Documentation* opens the folder with the other guides.
