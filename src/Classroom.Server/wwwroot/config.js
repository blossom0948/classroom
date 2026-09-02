const isLocalClassroom = ["localhost", "127.0.0.1"].includes(window.location.hostname);

window.CLASSROOM_CONFIG = Object.freeze({
  // The Pages proxy cannot reliably retain a newly issued session cookie
  // after the Firebase Google redirect on every managed browser.  Keep the
  // public console on the established, server-revocable API bearer route so
  // a completed Google sign-in reaches the classroom instead of landing.
  // Cookie mode remains available for a future custom same-site deployment.
  apiOrigin: isLocalClassroom ? window.location.origin : "https://classroom-api.blossom0948.cloud",
  cookieSession: false,
  studentInstallerUrl: "https://classroom-2en.pages.dev/student",
  firebase: {
    apiKey: "AIzaSyAYjzmqcVVIgBFgpzji7MOn2NVfl-B2N3c",
    authDomain: "classroom-production-52ae6.firebaseapp.com",
    projectId: "classroom-production-52ae6",
    storageBucket: "classroom-production-52ae6.firebasestorage.app",
    messagingSenderId: "151092222190",
    appId: "1:151092222190:web:841e2701677167c83bfbff"
  }
});
