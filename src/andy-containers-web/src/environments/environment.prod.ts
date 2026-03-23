export const environment = {
  production: true,
  apiUrl: 'https://YOUR_PRODUCTION_URL/api',
  oidc: {
    authority: 'https://YOUR_OIDC_AUTHORITY',
    clientId: 'YOUR_CLIENT_ID',
    redirectUrl: 'https://YOUR_PRODUCTION_URL/callback',
    postLogoutRedirectUri: 'https://YOUR_PRODUCTION_URL',
    scope: 'openid profile email roles',
  },
};
