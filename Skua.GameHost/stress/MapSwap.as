package {
import flash.display.*;
import flash.events.*;
import flash.net.URLRequest;
import flash.system.System;
import flash.utils.Timer;
// AQW's map change: World.cleanupMap closes the map's Loader, calls unloadAndStop(true) and System.gc(), and
// loads the next map into the same Loader. MapChild.as is the map. A map lasts 12 frames, every fourth one only 5, so
// that two collection requests fall within ten frames; every third map is replaced at once, its load still in
// flight. Every fifth map is unloaded once its content is in the Loader but before its init, and the next map loads
// only QUIET_FRAMES later, so that nothing but this movie stays resident in between. Each change also unloads
// an image Loader of the parent's own, as AQW's mail window does, and the parent's stage enterFrame listener and
// Timer must keep running throughout.
public class MapSwap extends Sprite {
    [Embed(source="MapArt.png", mimeType="application/octet-stream")] private static const Art:Class;
    private static const FRAMES_PER_PARENT_TRACE:int = 24;
    private static const QUIET_FRAMES:int = 48;
    private var loader:Loader = new Loader();
    private var image:Loader = new Loader();
    private var timer:Timer = new Timer(50);
    private var cycle:int = 0;
    private var frames:int = -1;
    private var quiet:int = -1;
    private var stageFrames:int = 0;
    private var ticks:int = 0;
    public function MapSwap() {
        addChild(loader);
        addChild(image);
        loader.contentLoaderInfo.addEventListener(Event.INIT, onInit);
        stage.addEventListener(Event.ENTER_FRAME, onStageFrame);
        timer.addEventListener(TimerEvent.TIMER, onTick);
        timer.start();
        addEventListener(Event.ENTER_FRAME, onFrame);
        trace("parent traces every " + FRAMES_PER_PARENT_TRACE + " frames at " + stage.frameRate + " fps");
        load();
    }
    private function onStageFrame(e:Event):void {
        if (++stageFrames % FRAMES_PER_PARENT_TRACE == 0) trace("parent frames " + stageFrames + " ticks " + ticks);
    }
    private function onTick(e:TimerEvent):void { ticks++; }
    private function onInit(e:Event):void {
        Object(loader.content).id = cycle;
        trace("map " + cycle + " loaded");
        frames = 0;
    }
    private function load():void {
        loader.load(new URLRequest("MapChild.swf"));
        image.loadBytes(new Art());
    }
    private function unload():void {
        try {
            loader.close();
        } catch (err:Error) {
        }
        loader.unloadAndStop(true);
        image.unloadAndStop(true);
        System.gc();
        cycle++;
    }
    private function change():void {
        unload();
        load();
    }
    private function onFrame(e:Event):void {
        if (quiet >= 0 && ++quiet == QUIET_FRAMES) {
            quiet = -1;
            trace("quiet spell over");
            load();
        }
        if (frames < 0 && quiet < 0 && cycle % 5 == 4 && loader.content != null) {
            trace("map " + cycle + " unloaded before init");
            unload();
            quiet = 0;
        }
        if (frames < 0) return;
        loader.contentLoaderInfo.sharedEvents.dispatchEvent(new Event("ping"));
        if (++frames < (cycle % 4 == 3 ? 5 : 12)) return;
        frames = -1;
        trace("map " + cycle + " unloaded");
        change();
        if (cycle % 3 == 0) {
            trace("map " + cycle + " replaced while loading");
            change();
        }
    }
}
}
