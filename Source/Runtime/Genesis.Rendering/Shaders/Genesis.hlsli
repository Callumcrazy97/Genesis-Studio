// Shared shader-editor / material constants (register b4 matches SilkNetDx11RenderController).
cbuffer ShaderEditorConstants : register(b4)
{
    float Time;
    float Frame;
    float ResX;
    float ResY;
};

#ifndef GENESIS_SPRITE_PREVIEW_BINDINGS
#define GENESIS_SPRITE_PREVIEW_BINDINGS
Texture2D    SpriteTex  : register(t0);
SamplerState SpriteSamp : register(s0);
#endif
