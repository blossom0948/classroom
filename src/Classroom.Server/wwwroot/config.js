const isLocalClassroom = ["localhost", "127.0.0.1"].includes(window.location.hostname);

window.CLASSROOM_CONFIG = Object.freeze({
  // Production Pages routes API calls through the same origin so the session
  // can stay in an HttpOnly cookie. Local development keeps the direct API
  // fallback for the existing bearer-token test workflow.
  apiOrigin: isLocalClassroom ? window.location.origin : window.location.origin,
  cookieSession: !isLocalClassroom,
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
