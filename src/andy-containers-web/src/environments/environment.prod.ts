export const environment = {
  production: true,
  apiUrl: '/api',
  oidc: {
    authority: 'https://localhost:5001',
    clientId: 'andy-containers-web',
    redirectUrl: '/callback',
    postLogoutRedirectUri: '/',
    scope: 'openid profile email urn:andy-containers-api offline_access',
  },
};
