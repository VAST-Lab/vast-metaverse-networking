Module['preRun'] = Module['preRun'] || [];
Module['preRun'].push(function () {
    if (typeof getWasmTableEntry !== "undefined" && typeof Proxy !== "undefined") {
        var handler = {
            get: function (target, prop) {
                if (typeof prop === "string" && prop.indexOf("dynCall_") === 0 && !(prop in target)) {
                    target[prop] = function (ptr) {
                        var args = Array.prototype.slice.call(arguments, 1);
                        return getWasmTableEntry(ptr).apply(null, args);
                    };
                }
                return target[prop];
            }
        };
        Module = new Proxy(Module, handler);
    }
});