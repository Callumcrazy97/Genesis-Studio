using Genesis.Application.Editors.Image.Imaging;

namespace Genesis.Application.Editors.Image.Dialogs;

/// <summary>Feature counterparts implemented on the current RGBA pipeline, without old IDE code.</summary>
public static class LegacyImageEffectCatalog
{
    public static IReadOnlyList<ImageEffectDefinition> All { get; } =
    [
        new("Colourise", [new("colour","Tint colour",EffectParamType.Colour,Color.Red),new("strength","Strength",EffectParamType.Slider,50,0,100)],
            (pixels,_,_,p)=>ImageAdjustmentOperations.Tint(pixels,(Color)p["colour"],Value(p,"strength")/100f)),
        new("Fade", [new("amount","Fade %",EffectParamType.Slider,50,0,100)],
            (pixels,_,_,p)=>ImageAdjustmentOperations.Fade(pixels,Value(p,"amount")/100f)),
        new("Palette Cycler / Auto Colour Swap", [new("shift","Hue shift",EffectParamType.Slider,30,0,360)],
            (pixels,_,_,p)=>ImageAdvancedOperations.AdjustHsv(pixels,Value(p,"shift"),1,1)),
        new("Generate LOD", [new("scale","Scale %",EffectParamType.Slider,50,10,90)],
            (pixels,w,h,p)=>ImageAdjustmentOperations.ReduceDetail(pixels,w,h,Value(p,"scale"))),
        new("Pixel Depth Mapper", [new("depth","Depth scale",EffectParamType.Slider,50,0,100)],
            (pixels,_,_,p)=>ImageAdjustmentOperations.DepthContrast(pixels,Value(p,"depth")/100f)),
        new("Edge Enhance", [], (pixels,w,h,_)=>ImageAdjustmentOperations.Sharpen(pixels,w,h,true)),
        new("Sharpen", [], (pixels,w,h,_)=>ImageAdjustmentOperations.Sharpen(pixels,w,h,false)),
        new("Selective Blur", [new("strength","Strength",EffectParamType.Slider,50,0,100),new("threshold","Threshold",EffectParamType.Slider,30,0,255)],
            (pixels,w,h,p)=>ImageAdjustmentOperations.SelectiveBlur(pixels,w,h,Value(p,"strength")/100f,Value(p,"threshold"))),
        new("RGSSAA / Rotated-grid smoothing", [new("samples","Samples",EffectParamType.Slider,4,1,8)],
            (pixels,w,h,p)=>ImageAdjustmentOperations.RotatedGridSmooth(pixels,w,h,Value(p,"samples"))),
    ];

    private static int Value(IReadOnlyDictionary<string, object> values, string name) => Convert.ToInt32(values[name]);
}