
mergeInto(LibraryManager.library, {
    RequestMicrophone: function (objPtr, methodPtr) {
        var obj = UTF8ToString(objPtr);
        var method = UTF8ToString(methodPtr);

        if (!navigator.mediaDevices ||
            !navigator.mediaDevices.getUserMedia) {
            SendMessage(obj, method, "unsupported");
            return;
        }

        navigator.mediaDevices.getUserMedia({ audio: true })
            .then(function (stream) {
                window.vivoxIndicatorStream = stream;

                var AudioContextClass =
                    window.AudioContext || window.webkitAudioContext;

                if (!AudioContextClass) {
                    SendMessage(obj, method, "unsupported");
                    return;
                }

                var context = new AudioContextClass();
                var source = context.createMediaStreamSource(stream);
                var analyser = context.createAnalyser();

                analyser.fftSize = 512;
                source.connect(analyser);

                window.vivoxIndicatorAnalyser = analyser;
                window.vivoxIndicatorContext = context;

                SendMessage(obj, method, "granted");
            })
            .catch(function (e) {
                SendMessage(obj, method, "error:" + e.name);
            });
    },

    GetMicrophoneLevel: function () {
        var analyser = window.vivoxIndicatorAnalyser;

        if (!analyser) return 0;

        var samples = new Uint8Array(analyser.fftSize);
        analyser.getByteTimeDomainData(samples);

        var sum = 0;

        for (var i = 0; i < samples.length; i++) {
            var value = (samples[i] - 128) / 128;
            sum += value * value;
        }

        return Math.sqrt(sum / samples.length);
    },

    StopMicrophoneMonitor: function () {
        if (window.vivoxIndicatorStream) {
            window.vivoxIndicatorStream.getTracks().forEach(
                function (track) {
                    track.stop();
                }
            );
        }

        if (window.vivoxIndicatorContext) {
            window.vivoxIndicatorContext.close();
        }

        window.vivoxIndicatorStream = null;
        window.vivoxIndicatorAnalyser = null;
        window.vivoxIndicatorContext = null;
    }
});
