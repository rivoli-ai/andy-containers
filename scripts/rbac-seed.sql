-- ============================================================
-- Andy Containers - RBAC Seed Data
-- Run against the andy_rbac PostgreSQL database:
--   docker exec andy-rbac-db psql -U postgres -d andy_rbac -f /scripts/rbac-seed.sql
-- ============================================================

-- Step 1: Register the application
INSERT INTO applications ("Id", "Code", "Name", "Description", "CreatedAt")
VALUES (
  'b2c3d4e5-f6a7-8901-bcde-f12345678901',
  'containers',
  'Andy Containers',
  'Development container management platform',
  NOW()
) ON CONFLICT ("Code") DO NOTHING;

-- Step 2: Create resource types
INSERT INTO resource_types ("Id", "ApplicationId", "Code", "Name", "Description", "SupportsInstances") VALUES
('22222222-1111-1111-1111-111111111001', 'b2c3d4e5-f6a7-8901-bcde-f12345678901', 'container', 'Container', 'Container lifecycle, exec, resize', true),
('22222222-1111-1111-1111-111111111002', 'b2c3d4e5-f6a7-8901-bcde-f12345678901', 'template', 'Template', 'Template catalog', false),
('22222222-1111-1111-1111-111111111003', 'b2c3d4e5-f6a7-8901-bcde-f12345678901', 'workspace', 'Workspace', 'Workspace management', false),
('22222222-1111-1111-1111-111111111004', 'b2c3d4e5-f6a7-8901-bcde-f12345678901', 'provider', 'Provider', 'Infrastructure providers', false),
('22222222-1111-1111-1111-111111111005', 'b2c3d4e5-f6a7-8901-bcde-f12345678901', 'settings', 'Settings', 'API keys and monitoring config', false),
('22222222-1111-1111-1111-111111111006', 'b2c3d4e5-f6a7-8901-bcde-f12345678901', 'image', 'Image', 'Container images', false)
ON CONFLICT DO NOTHING;

-- Step 3: Create permissions (cross join resource types with actions)
INSERT INTO permissions ("Id", "ResourceTypeId", "ActionId", "Description")
SELECT gen_random_uuid(), rt."Id", a."Id", rt."Name" || ' ' || a."Name"
FROM resource_types rt
CROSS JOIN actions a
JOIN applications app ON rt."ApplicationId" = app."Id"
WHERE app."Code" = 'containers'
AND (
  (rt."Code" = 'container' AND a."Code" IN ('read', 'write', 'delete', 'execute'))
  OR (rt."Code" = 'template' AND a."Code" IN ('read', 'write', 'delete'))
  OR (rt."Code" = 'workspace' AND a."Code" IN ('read', 'write', 'delete'))
  OR (rt."Code" = 'provider' AND a."Code" IN ('read', 'manage'))
  OR (rt."Code" = 'settings' AND a."Code" IN ('read', 'write'))
  OR (rt."Code" = 'image' AND a."Code" IN ('read', 'write', 'delete'))
)
ON CONFLICT DO NOTHING;

-- Step 4: Create roles
INSERT INTO roles ("Id", "ApplicationId", "Code", "Name", "Description", "IsSystem", "CreatedAt") VALUES
('33333333-2222-2222-2222-222222222001', 'b2c3d4e5-f6a7-8901-bcde-f12345678901', 'admin', 'Administrator', 'Full access to Andy Containers', false, NOW()),
('33333333-2222-2222-2222-222222222002', 'b2c3d4e5-f6a7-8901-bcde-f12345678901', 'user', 'User', 'Standard user access (read + execute)', false, NOW())
ON CONFLICT DO NOTHING;

-- Step 5: Assign permissions to roles

-- Admin: all permissions
INSERT INTO role_permissions ("RoleId", "PermissionId")
SELECT '33333333-2222-2222-2222-222222222001', p."Id"
FROM permissions p
JOIN resource_types rt ON p."ResourceTypeId" = rt."Id"
JOIN applications app ON rt."ApplicationId" = app."Id"
WHERE app."Code" = 'containers'
ON CONFLICT DO NOTHING;

-- User: read + execute (can use containers but not manage templates/providers)
INSERT INTO role_permissions ("RoleId", "PermissionId")
SELECT '33333333-2222-2222-2222-222222222002', p."Id"
FROM permissions p
JOIN resource_types rt ON p."ResourceTypeId" = rt."Id"
JOIN actions a ON p."ActionId" = a."Id"
JOIN applications app ON rt."ApplicationId" = app."Id"
WHERE app."Code" = 'containers'
AND (
  a."Code" = 'read'
  OR (rt."Code" = 'container' AND a."Code" IN ('write', 'execute'))
  OR (rt."Code" = 'workspace' AND a."Code" = 'write')
  OR (rt."Code" = 'settings' AND a."Code" = 'write')
)
ON CONFLICT DO NOTHING;

-- Step 6: Assign admin role to a user
-- First, find your user ID from Andy.Auth:
--   docker exec andy-auth-db psql -U postgres -d andy_auth -c 'SELECT "Id", "UserName", "Email" FROM "AspNetUsers";'
--
-- Then create a subject and assign the admin role:
--   INSERT INTO subjects ("Id", "ExternalId", "Provider", "Type", "Email", "DisplayName", "IsActive", "CreatedAt")
--   VALUES (gen_random_uuid(), '<andy-auth-user-id>', 'andy-auth', 0, '<email>', '<name>', true, NOW());
--
--   INSERT INTO subject_roles ("Id", "SubjectId", "RoleId", "GrantedAt")
--   SELECT gen_random_uuid(), s."Id", '33333333-2222-2222-2222-222222222001', NOW()
--   FROM subjects s WHERE s."ExternalId" = '<andy-auth-user-id>';
