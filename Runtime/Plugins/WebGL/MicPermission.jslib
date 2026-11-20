mergeInto(LibraryManager.library, {
  RequestMicrophone: function (objPtr, methodPtr) {
    var obj = UTF8ToString(objPtr);
    var method = UTF8ToString(methodPtr);
    if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) {
      SendMessage(obj, method, "unsupported");
      return;
    }
    navigator.mediaDevices.getUserMedia({ audio: true })
      .then(function (stream) {
        stream.getTracks().forEach(function (t) { t.stop(); });
        SendMessage(obj, method, "granted");
      })
      .catch(function (e) {
        SendMessage(obj, method, "error:" + e.name);
      });
  }
});