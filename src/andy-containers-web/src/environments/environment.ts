export const environment = {
  production: false,
  apiUrl: '/api',
  oidc: {
    authority: 'https://localhost:5001',
    clientId: 'andy-containers-web',
    redirectUrl: 'https://localhost:4200/callback',
    postLogoutRedirectUri: 'https://localhost:4200',
    scope: 'openid profile email roles',
  },
};
