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
      "auth/weak-password": "비밀번호는 6자 이상으로 설정해 주세요.",
      "auth/email-already-in-use": "이미 가입된 이메일입니다. 로그인해 주세요.",
      "auth/user-not-found": "가입된 계정을 찾지 못했습니다.",
      "auth/wrong-password": "이메일 또는 비밀번호가 올바르지 않습니다.",
      "auth/invalid-credential": "이메일 또는 비밀번호가 올바르지 않습니다.",
      "auth/popup-closed-by-user": "Google 로그인 창을 닫았습니다.",
      "auth/cancelled-popup-request": "Google 로그인을 취소했습니다.",
      "auth/popup-blocked": "Google 로그인 창을 열지 못했습니다. 현재 창에서 다시 시도해 주세요.",
      "auth/operation-not-supported-in-this-environment": "현재 브라우저에서 Google 로그인을 시작할 수 없습니다. 일반 브라우저에서 다시 시도해 주세요.",
      "auth/account-exists-with-different-credential": "이미 다른 로그인 방식으로 가입된 이메일입니다. 이메일 로그인으로 먼저 로그인해 주세요.",
      "auth/operation-not-allowed": "Firebase에서 이 로그인 방식이 아직 활성화되지 않았습니다.",
      "auth/unauthorized-domain": "현재 사이트 도메인이 Firebase 승인 도메인에 없습니다.",
      "auth/network-request-failed": "Firebase에 연결하지 못했습니다. 네트워크를 확인해 주세요.",
      "auth/invalid-api-key": "Firebase 웹 설정이 올바르지 않습니다. 관리자에게 설정을 확인해 주세요.",
      "auth/app-not-authorized": "이 사이트가 Firebase에 승인되지 않았습니다. 관리자에게 승인 도메인을 확인해 주세요.",
      "auth/redirect-cancelled-by-user": "Google 로그인을 취소했습니다.",
      "auth/internal-error": "Google 로그인 연결을 시작하지 못했습니다. 페이지를 새로고침한 뒤 다시 시도해 주세요."
    };
    const friendly = new Error(messages[error?.code] || error?.message || "Firebase 인증에 실패했습니다.");
    friendly.code = error?.code;
    return friendly;
  }

  async function toSessionPayload(user) {
    return {
      // A freshly completed OAuth flow already has a valid token. Forcing a
      // refresh here adds an unnecessary network dependency and can turn a
      // successful popup into auth/internal-error on managed school networks.
      idToken: await user.getIdToken(),
      email: user.email || "",
      displayName: user.displayName || ""
    };
  }

  function logAuthFailure(stage, error) {
    console.error(`[Classroom] Firebase auth failure stage=${stage} code=${error?.code || "unknown"} message=${error?.message || "unknown"}`);
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
    const auth = getAuth();
    const provider = new firebase.auth.GoogleAuthProvider();
    provider.setCustomParameters({ prompt: "select_account" });
    try {
      // A popup keeps the Firebase credential in the same JavaScript context,
      // so the app can exchange it with Classroom immediately. Some managed
      // school browsers block popups; only those cases fall back to redirect.
      const result = await auth.signInWithPopup(provider);
      return toSessionPayload(result.user);
    } catch (error) {
      logAuthFailure("google-sign-in", error);
      const shouldUseRedirect = [
        "auth/popup-blocked",
        "auth/cancelled-popup-request",
        "auth/network-request-failed",
        "auth/internal-error"
      ].includes(error?.code);
      if (shouldUseRedirect) {
        try {
          await auth.signInWithRedirect(provider);
          return null;
        } catch (redirectError) {
          logAuthFailure("google-redirect-fallback", redirectError);
          throw friendlyError(redirectError);
        }
      }
      throw friendlyError(error);
    }
  }

  async function consumeRedirectResult() {
    try {
      const result = await getAuth().getRedirectResult();
      return result?.user ? toSessionPayload(result.user) : null;
    } catch (error) {
      logAuthFailure("google-redirect-result", error);
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

  async function lookupAccount(email) {
    try {
      const methods = await getAuth().fetchSignInMethodsForEmail(email.trim());
      return Array.isArray(methods) ? methods : [];
    } catch (error) {
      throw friendlyError(error);
    }
  }

  window.ClassroomFirebaseAuth = Object.freeze({
    isConfigured,
    signInEmail,
    signUpEmail,
    signInGoogle,
    consumeRedirectResult,
    sendPasswordReset,
    lookupAccount,
    signOut: () => getAuth().signOut()
  });
})();
