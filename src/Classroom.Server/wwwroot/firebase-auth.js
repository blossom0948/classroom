(() => {
  const firebaseConfig = window.CLASSROOM_CONFIG?.firebase || {};
  const requiredKeys = ["apiKey", "authDomain", "projectId", "appId"];

  function isConfigured() {
    return Boolean(window.firebase)
      && requiredKeys.every((key) => String(firebaseConfig[key] || "").trim());
  }

  function getAuth() {
    if (!isConfigured()) {
      throw new Error("Firebase 인증 설정이 아직 완료되지 않았습니다.");
    }
    if (!firebase.apps.length) {
      firebase.initializeApp(firebaseConfig);
    }
    return firebase.auth();
  }

  function friendlyError(error) {
    const messages = {
      "auth/invalid-email": "이메일 주소 형식을 확인해 주세요.",
      "auth/missing-password": "비밀번호를 입력해 주세요.",
      "auth/weak-password": "비밀번호는 12자 이상으로 설정해 주세요.",
      "auth/email-already-in-use": "이미 가입된 이메일입니다. 로그인해 주세요.",
      "auth/user-not-found": "가입된 계정을 찾지 못했습니다.",
      "auth/wrong-password": "이메일 또는 비밀번호가 올바르지 않습니다.",
      "auth/invalid-credential": "이메일 또는 비밀번호가 올바르지 않습니다.",
      "auth/popup-closed-by-user": "Google 로그인 창을 닫았습니다.",
      "auth/popup-blocked": "브라우저가 Google 로그인 팝업을 막았습니다. 팝업을 허용해 주세요.",
      "auth/operation-not-allowed": "Firebase에서 이 로그인 방식이 아직 활성화되지 않았습니다.",
      "auth/unauthorized-domain": "현재 사이트 도메인이 Firebase 승인 도메인에 없습니다.",
      "auth/network-request-failed": "Firebase에 연결하지 못했습니다. 네트워크를 확인해 주세요."
    };
    return new Error(messages[error?.code] || error?.message || "Firebase 인증에 실패했습니다.");
  }

  async function toSessionPayload(user) {
    return {
      idToken: await user.getIdToken(true),
      email: user.email || "",
      displayName: user.displayName || ""
    };
  }

  async function signInEmail(email, password) {
    try {
      const credential = await getAuth().signInWithEmailAndPassword(email.trim(), password);
      return toSessionPayload(credential.user);
    } catch (error) {
      throw friendlyError(error);
    }
  }

  async function signUpEmail(email, password, displayName) {
    try {
      const credential = await getAuth().createUserWithEmailAndPassword(email.trim(), password);
      if (displayName.trim()) {
        await credential.user.updateProfile({ displayName: displayName.trim() });
      }
      return toSessionPayload(credential.user);
    } catch (error) {
      throw friendlyError(error);
    }
  }

  async function signInGoogle() {
    try {
      const provider = new firebase.auth.GoogleAuthProvider();
      provider.setCustomParameters({ prompt: "select_account" });
      const credential = await getAuth().signInWithPopup(provider);
      return toSessionPayload(credential.user);
    } catch (error) {
      throw friendlyError(error);
    }
  }

  async function sendPasswordReset(email) {
    try {
      await getAuth().sendPasswordResetEmail(email.trim());
    } catch (error) {
      throw friendlyError(error);
    }
  }

  window.ClassroomFirebaseAuth = Object.freeze({
    isConfigured,
    signInEmail,
    signUpEmail,
    signInGoogle,
    sendPasswordReset,
    signOut: () => getAuth().signOut()
  });
})();
