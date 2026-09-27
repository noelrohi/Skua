package {
import flash.display.*;
import flash.events.*;
import flash.filters.*;
public class Stress2 extends Sprite {
    private var src:Sprite = new Sprite();
    private var bmp:BitmapData = new BitmapData(128, 128, true, 0);
    private var frames:int = 0;
    public function Stress2() {
        // An "avatar": many parts, each with filters, masks and blend modes.
        for (var i:int = 0; i < 20; i++) {
            var part:Sprite = new Sprite();
            part.graphics.beginFill(0x3366ff + i * 100); part.graphics.drawRect(i * 3, i * 2, 40, 40); part.graphics.endFill();
            part.filters = [new GlowFilter(0xffffff, 1, 4, 4), new DropShadowFilter()];
            part.blendMode = (i % 2) ? BlendMode.MULTIPLY : BlendMode.LAYER;
            var m:Shape = new Shape(); m.graphics.beginFill(0); m.graphics.drawCircle(20 + i, 20, 18); m.graphics.endFill();
            part.addChild(m); part.mask = m;
            src.addChild(part);
        }
        addChild(new Bitmap(bmp));
        addEventListener(Event.ENTER_FRAME, onFrame);
    }
    private function onFrame(e:Event):void {
        for (var i:int = 0; i < 60; i++) bmp.draw(src);
        frames++;
        if (frames % 30 == 0) trace("frames " + frames);
    }
}
}
