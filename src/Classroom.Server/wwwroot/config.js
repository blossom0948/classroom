const isLocalClassroom = ["localhost", "127.0.0.1"].includes(window.location.hostname);

window.CLASSROOM_CONFIG = Object.freeze({
  // Keep the production bearer session on the existing API origin. This is
  // compatible with already-installed consoles and avoids losing a fresh
  // login when a Pages proxy cannot persist a Set-Cookie response.
  apiOrigin: isLocalClassroom ? window.location.origin : "https://classroom-api.blossom0948.cloud",
  cookieSession: false,
  studentInstallerUrl: "https://github.com/blossom0948/classroom/releases/latest/download/Classroom.Student.Setup.exe",
  firebase: {
    apiKey: "AIzaSyAYjzmqcVVIgBFgpzji7MOn2NVfl-B2N3c",
    authDomain: "classroom-production-52ae6.firebaseapp.com",
    projectId: "classroom-production-52ae6",
    storageBucket: "classroom-production-52ae6.firebasestorage.app",
    messagingSenderId: "151092222190",
    appId: "1:151092222190:web:841e2701677167c83bfbff"
  }
});
