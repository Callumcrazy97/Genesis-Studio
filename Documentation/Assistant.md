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
- reading text in images (Nova's OCR);
- a description of a running game's scene;
- tools that create other kinds of resources;
- an evaluation set for comparing models.

The check `Genesis.Application.Headless.exe --assistant-check --with-model` loads the first model
found and asks it for one piece of code.
