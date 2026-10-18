package {
import flash.display.*;
import flash.utils.setTimeout;
// The child Cancelled.as loads. It throws from an anonymous callback, so the error's stack names no class of its own.
public class CancelledChild extends Sprite {
    public function CancelledChild() {
        setTimeout(function():void { throw new Error("thrown by the child"); }, 20);
    }
}
}
