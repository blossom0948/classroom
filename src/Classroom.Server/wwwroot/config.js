const isLocalClassroom = ["localhost", "127.0.0.1"].includes(window.location.hostname);

window.CLASSROOM_CONFIG = Object.freeze({
  // Production requests travel through the Pages proxy. That lets the
  // browser keep the authentication token in an HttpOnly, Secure host cookie
  // instead of exposing a bearer token to localStorage.
  apiOrigin: window.location.origin,
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
