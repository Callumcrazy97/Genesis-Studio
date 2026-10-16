# AI Assistant

**View › AI Assistant** opens a panel where a local AI model answers questions about PGSL, or writes
PGSL that Genesis checks before you see it. It runs on this PC: nothing is sent anywhere.

## Set up

- **Engine:** the assistant runs models with llama.cpp's `llama-server.exe`. It finds the one Nova
  installed (`%LocalAppData%\NovaStudio\runtime`), or one in Genesis's `Tools\llama` folder; ⚙ ›
  *Locate llama-server…* chooses another.
- **Model:** any GGUF file. The list shows Nova's models (`%LocalAppData%\NovaStudio\models`) and
  `Documents\Genesis Models`; *Browse for a model…* picks any other. Small models (Gemma 2B, Qwen3 4B)
  follow instructions best.
- ⚙ also sets **Use the graphics card** and the **context length** (8K by default). Changes apply
  the next time the model starts.

Press **Start** to load the model; **Stop** frees its memory. Studio stops it when it closes.

## Ask, or write PGSL

- **Ask:** questions about PGSL and Genesis. Each question comes with the PGSL commands that match
  it best (found by keyword in the full command list), so a small model's context holds what matters.
- **Write PGSL:** describe what the code should do. The model must answer as JSON (`code`, `notes`),
  which llama.cpp enforces. Genesis then runs the PGSL checker on the code, and also flags any
  command that does not exist. If there are problems, they go back to the model, up to three times.
  The result shows whether it passed; **Copy code** or **Save as Script…** keeps it.

## Opening and looking

In **Ask**, the model can take up to six steps before it answers. Each step is JSON that llama.cpp
enforces: `answer`, `open` or `look`.
- **open** finds the resource the user means by name, even when they only say part of it ("the main
  room", "player sprite"). It tries the exact name first, then a name that contains what was said,
  then shared words; a kind word such as room, sprite, object or script breaks ties. The resource
  then opens in its editor. The model is given the project's resource names.
- **look** takes a picture of one of these:
  - `window`: the whole Studio window;
  - `region:x,y,w,h`: an area of the window, in pixels from its top-left corner;
  - `image:NAME`: an image resource's first frame;
  - anything else: the open editor.

  Window and editor pictures come from the window itself (PrintWindow), so another app on top of
  Studio does not get into the picture.
- **What the model receives:**
  - **Every model:** the picture described in words, and the text in it read by **Genesis Nova
    OCR**. Each line of text comes with its position and a confidence score.
  - **A vision model:** also the picture itself. A model counts as having vision when an
    `*mmproj*.gguf` file sits beside it; that file is passed to llama-server as `--mmproj`.

Genesis Nova OCR is Tesseract 5 with its English LSTM data (`tessdata\eng.traineddata`), the same
engine Nova uses. Small pictures such as sprites are scaled up first, and transparent pixels are
read as white. Attaching an **Image…** also adds the text found in it.

Checked on 16 Oct 2026 (`--ocr-check <folder>`):
- **A 160×40 sprite:** it read "SCORE 120" at 94% confidence.
- **A text window:** it read "Genesis OCR window test 42" at 96% and "Room: Main Room Objects: 3"
  at 87%, with their positions.

## Make a game

Describe a small 2D game ("a few coloured squares that move around and bounce off the edges"). The
model answers with one JSON document: sprites (a square or circle, its size and colour), objects
(the sprite each shows, and its Create and Step code) and a room (size, background and where each
object is placed). Genesis checks it before anything is written:
- every sprite, object and room has its own name, unused in the project;
- objects use sprites, and the room places objects, that exist;
- each object's Create and Step code passes the strict PGSL check Run uses, with no invented commands;
- an object that moves by a variable its Create never sets would stand still, so that is reported too.

Problems go back to the model, up to three times. Then Genesis writes exactly what the model
specified: the sprites' pictures, the objects and their event code, and the room, which becomes the
start room. Press Run.

Checked on 16 Oct 2026 with Qwen3-4B (`--assistant-game`): it built three moving squares in an
800×600 room in three rounds. The project passed the strict check, and two screenshots three
seconds apart showed the squares moving and staying inside the room.

## Context

- **Image…** describes a picture in words for models without vision: its size, transparent area,
  visible bounds, main colours, brightness, and whether it looks like a strip of animation frames.
  PNG, JPG, BMP and GIF files.
- **Room…** lists a room's size, whether it is 2D or 3D, and what is placed in it, with positions.
- **Script…** adds a PGSL file's text.

**Clear** forgets the conversation and the attachments. Only the last few turns are sent each
time, because small models have short contexts.

## Not yet

These are planned:
- a description of a running game's scene;
- making sounds, particles or 3D games (Make a game covers 2D sprites, objects and a room);
- an evaluation set for comparing models.

The check `Genesis.Application.Headless.exe --assistant-check --with-model` loads the first model
found and asks it for one piece of code.
