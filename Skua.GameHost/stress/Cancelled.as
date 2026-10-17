package {
import flash.display.*;
import flash.events.*;
import flash.net.URLRequest;
// AQW's Game.registerErrorSuppression: the root cancels every uncaught error. This movie and the child it loads
// (CancelledChild.as) each throw from an anonymous callback, whose stack names no class, so only the movie the host
// reports with the error says whose code threw it.
public class Cancelled extends Sprite {
    private var loader:Loader = new Loader();
    private var frames:int = 0;
    public function Cancelled() {
        loaderInfo.uncaughtErrorEvents.addEventListener(UncaughtErrorEvent.UNCAUGHT_ERROR, function(e:UncaughtErrorEvent):void {
            e.preventDefault();
        });
        addChild(loader);
        loader.load(new URLRequest("CancelledChild.swf"));
        addEventListener(Event.ENTER_FRAME, function(e:Event):void {
            if (++frames == 5) throw new Error("thrown by the root");
            if (frames == 10) trace("done");
        });
    }
}
}
