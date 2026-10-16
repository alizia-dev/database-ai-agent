export const PERMISSIONS = [
  'database.view',
  'database.manage',
  'database.analyze',
  'tools.view',
  'tools.manage',
  'tools.execute',
  'knowledge.view',
  'knowledge.manage',
  'chat.use',
  'billing.view',
  'billing.manage',
  'users.view',
  'users.manage',
  'audit.view',
  'settings.manage',
] as const;

export type Permission = (typeof PERMISSIONS)[number];

export const ROLES = ['Owner', 'Admin', 'Manager', 'Analyst', 'Viewer'] as const;
export type Role = (typeof ROLES)[number];

export const PERMISSION_LABELS: Record<Permission, string> = {
  'database.view': 'View database connections',
  'database.manage': 'Manage database connections',
  'database.analyze': 'Analyze database schema',
  'tools.view': 'View AI tools',
  'tools.manage': 'Approve and configure AI tools',
  'tools.execute': 'Run AI tools in chat',
  'knowledge.view': 'View knowledge base',
  'knowledge.manage': 'Manage knowledge base',
  'chat.use': 'Use AI chat',
  'billing.view': 'View billing',
  'billing.manage': 'Manage subscription and payments',
  'users.view': 'View team members',
  'users.manage': 'Invite and manage team members',
  'audit.view': 'View audit logs',
  'settings.manage': 'Edit workspace settings',
};

export interface CurrentUser {
  id: string;
  email: string;
  displayName: string;
  role: Role;
  permissions: Permission[];
}

export interface SessionResponse {
  accessToken: string;
  accessTokenExpiresAt: string;
  user: CurrentUser;
}

export interface MessageResponse {
  message: string;
}

export interface InvitationPreview {
  email: string;
  role: Role;
  workspaceName: string;
}
