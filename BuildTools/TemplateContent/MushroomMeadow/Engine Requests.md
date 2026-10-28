# Mushroom Meadow: engine requests (28 Oct)

Found while measuring the finished game. The game works around none of these; they are engine-side costs or gaps.

1. **Per-frame allocation (still 90-270 KB per frame; budget 25 KB).** Repro: any game calling these every frame, then `dotnet-trace --profile gc-verbose` and AllocStacks.
   - `CollisionRectangle` goes `SpriteCollisionBounds.Load` -> `ResourceCatalog.Find` (Replace/Concat/IsInside/HasLinkedParent) for every instance of the tested object on every call. About half of what is left. Cache bounds per resource.
   - `InstanceNearest` (`NearestOrFurthest`) builds a list of every living entity per call and string-matches each.
   - `InstanceFind` and `InstanceNumber` by name (`FindTargets`) walk every live instance and allocate per call.
2. **Play to first frame is 6-11 s** in a 60 fps run (room load itself 0.12-0.33 s). The startup asset step lists 630 assets for the first room; load only what the room uses. Engine speed bar: seconds, not 6-11.
3. **Window resize costs 100-800 ms per resize** in the profiled tours.
4. **Room Backgrounds stretch** (Main Menu and Credits backdrops, about 1.3x at 21:9). A "cover" option (scale to fill, crop the overflow) would fix it.
5. **Export dialog**: default the icon from the project's `projectIcon` (Mushroom Meadow ships `Mushroom Meadow.ico`; today it is picked by hand).
6. `game.run` refuses Escape; scripts use P, Backspace or Enter.
