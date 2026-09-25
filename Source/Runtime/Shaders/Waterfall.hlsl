// Waterfalls use the shared water material ABI. Water.hlsl builds a local tangent
// basis from the generated ribbon normal, scrolls two normal samples at different
// speeds, and adds UV-edge contact foam when RapidsIntensity is enabled.
#include "Water.hlsl"
