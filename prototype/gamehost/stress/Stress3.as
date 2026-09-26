package {
import flash.display.*;
import flash.events.*;
import flash.filters.*;
// One heavy stage: many masked, filtered, blended children rendered in a single frame (no BitmapData).
public class Stress3 extends Sprite {
    private var frames:int = 0;
    public function Stress3() {
        for (var i:int = 0; i < 1500; i++) {
            var part:Sprite = new Sprite();
            part.graphics.beginFill(0x3366ff + i); part.graphics.drawRect((i * 7) % 900, (i * 13) % 500, 30, 30); part.graphics.endFill();
            part.filters = [new GlowFilter(0xffffff, 1, 4, 4)];
            part.blendMode = BlendMode.LAYER;
            var m:Shape = new Shape(); m.graphics.beginFill(0); m.graphics.drawCircle((i * 7) % 900 + 15, (i * 13) % 500 + 15, 14); m.graphics.endFill();
            part.addChild(m); part.mask = m;
            addChild(part);
        }
        addEventListener(Event.ENTER_FRAME, function(e:Event):void { frames++; if (frames % 30 == 0) trace("frames " + frames); });
    }
}
}
