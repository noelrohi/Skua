package {
import flash.display.*;
import flash.events.*;
import flash.system.System;
import flash.utils.Dictionary;
// #13: AQW Game._colorCache pattern. A weak-key Dictionary caches every short-lived clip.
// Flash: dead keys disappear. Ruffle before 0015: every key stays forever.
public class WeakDict extends Sprite {
    private var weak:Dictionary = new Dictionary(true);
    private var strong:Dictionary = new Dictionary();
    private var keep:Sprite = new Sprite();          // a key that stays referenced
    private var cur:MovieClip;
    private var frames:int = 0;
    public function WeakDict() {
        weak[keep] = {typ: "kept"};
        weak["str"] = 1;                             // non-object key: always kept
        addEventListener(Event.ENTER_FRAME, onFrame);
    }
    private function onFrame(e:Event):void {
        if (cur) removeChild(cur);
        cur = new MovieClip();
        for (var i:int = 0; i < 3; i++) cur.addChild(new Shape());
        addChild(cur);
        weak[cur] = {avatarMC: this, typ: "a"};
        if (frames < 50) strong[new Sprite()] = frames;
        if (++frames % 60 == 0) {
            System.gc();
            var n:int = 0, s:int = 0;
            for (var k:* in weak) n++;
            for (var k2:* in strong) s++;
            trace("weak " + n + " strong " + s + " kept " + (weak[keep] != null) + " str " + weak["str"]);
        }
    }
}
}
