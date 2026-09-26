package {
import flash.display.*;
import flash.events.*;
import flash.filters.GlowFilter;
import flash.external.ExternalInterface;
public class Stress extends Sprite {
    private var src:Sprite = new Sprite();
    private var bmp:BitmapData = new BitmapData(64, 64, true, 0);
    private var frames:int = 0;
    public function Stress() {
        src.graphics.beginFill(0xff0000); src.graphics.drawCircle(32, 32, 30); src.graphics.endFill();
        src.filters = [new GlowFilter()];
        addChild(new Bitmap(bmp));
        addEventListener(Event.ENTER_FRAME, onFrame);
    }
    private function onFrame(e:Event):void {
        for (var i:int = 0; i < 200; i++) bmp.draw(src);
        frames++;
        if (frames % 30 == 0) trace("frames " + frames);
    }
}
}
