package {
import flash.display.*;
import flash.events.*;
import flash.utils.Timer;
// The map MapSwap.as loads: a stage enterFrame listener, a weak listener on its LoaderInfo's sharedEvents and a
// running Timer, each of which keeps an unloaded map alive and running unless unloadAndStop and weak listeners
// let go of it. The bitmap stands in for the map's art, and the image Loader for the pictures a map loads.
public class MapChild extends Sprite {
    [Embed(source="MapArt.png", mimeType="application/octet-stream")] private static const Art:Class;
    public var id:int = -1;
    private var timer:Timer = new Timer(50);
    private var art:BitmapData = new BitmapData(512, 512, false, 0x336699);
    private var picture:Loader = new Loader();
    public function MapChild() {
        if (stage) start();
        else addEventListener(Event.ADDED_TO_STAGE, start);
    }
    private function start(e:Event = null):void {
        removeEventListener(Event.ADDED_TO_STAGE, start);
        addChild(new Bitmap(art));
        addChild(picture);
        picture.loadBytes(new Art());
        stage.addEventListener(Event.ENTER_FRAME, onStageFrame);
        loaderInfo.sharedEvents.addEventListener("ping", onPing, false, 0, true);
        timer.addEventListener(TimerEvent.TIMER, onTimer);
        timer.start();
    }
    private function fired(what:String):void {
        if (id >= 0) trace("map " + id + " " + what);
    }
    private function onStageFrame(e:Event):void { fired("stage enterFrame"); }
    private function onPing(e:Event):void { fired("ping"); }
    private function onTimer(e:TimerEvent):void { fired("timer"); }
}
}
