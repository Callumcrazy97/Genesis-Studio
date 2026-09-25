namespace Genesis.Application.Editors.Image.Imaging;

public enum ImageFrameScope { ThisFrame, Range, AllFrames, PreviousFrames, NextFrames }

/// <summary>Inclusive, one-based frame numbers. Previous/next exclude the current frame.</summary>
public sealed record ImageFrameSelection(ImageFrameScope Scope=ImageFrameScope.ThisFrame,int First=1,int Last=1)
{
    public int[] Resolve(int current,int count)
    {
        if(count<1) return [];
        if(current<0 || current>=count) throw new ArgumentOutOfRangeException(nameof(current));
        (int first,int last)=Scope switch
        {
            ImageFrameScope.ThisFrame=>(current,current),
            ImageFrameScope.AllFrames=>(0,count-1),
            ImageFrameScope.PreviousFrames=>(0,current-1),
            ImageFrameScope.NextFrames=>(current+1,count-1),
            ImageFrameScope.Range when First>=1 && Last>=First && Last<=count=>(First-1,Last-1),
            _=>throw new ArgumentException("Choose an ordered frame range within the image.")
        };
        return last<first ? [] : Enumerable.Range(first,last-first+1).ToArray();
    }
}
