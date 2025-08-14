# Azure AD App Registration Fix

## Issue
The deployment is failing with error:
```
ServiceTreeValueMissing: ServiceManagementReference field is required for Create, but is missing in the request.
```

## Root Cause
Microsoft requires a **Service Tree ID** for all new Azure AD app registrations to track service ownership and compliance.

## Solution Options

### Option 1: Add Service Tree Reference (Recommended for Microsoft Internal)
1. **Get your Service Tree ID**:
   - Visit https://servicetree.microsoft.com
   - Register your service or find your existing service
   - Copy the Service Tree ID (usually a GUID)

2. **Update environment variable**:
   ```bash
   # In M365Agent/env/.env.dev
   SERVICE_TREE_ID=your-service-tree-id-here
   ```

3. **Deploy again**:
   ```bash
   cd M365Agent
   azd provision
   ```

### Option 2: Use Existing Azure AD App
If you already have an Azure AD app, skip creation:

1. **Update m365agents.local.yml** - Replace the `aadApp/create` section:
   ```yaml
   # Use existing Azure AD app instead of creating
   - uses: aadApp/update
     with:
       manifestPath: ./aad.manifest.json
       outputFilePath: ./build/aad.manifest.${{TEAMSFX_ENV}}.json
   ```

2. **Set environment variables manually**:
   ```bash
   # In M365Agent/env/.env.dev
   BOT_ID=your-existing-app-id
   SECRET_BOT_PASSWORD=your-existing-app-secret
   ```

### Option 3: External Azure AD App (Non-Microsoft)
For external developers, you can create the app manually:

1. **Manually create Azure AD app**:
   - Go to https://portal.azure.com → Azure Active Directory → App registrations
   - Create new registration with redirect URI for your bot endpoint
   - Generate client secret
   - Note down Application (client) ID and secret

2. **Comment out automatic creation** in m365agents.local.yml:
   ```yaml
   # # Create or reuse an existing Microsoft Entra application for bot.
   # - uses: aadApp/create
   #   with:
   #     name: peeop${{APP_NAME_SUFFIX}}
   #     generateClientSecret: true
   #     signInAudience: AzureADMultipleOrgs
   #     serviceManagementReference: "${{SERVICE_TREE_ID}}"
   ```

3. **Set the values manually** in .env.dev:
   ```bash
   BOT_ID=your-manual-app-id
   SECRET_BOT_PASSWORD=your-manual-app-secret
   ```

## Current Status
- ✅ Fixed: Added SERVICE_TREE_ID parameter to aadApp/create
- ⚠️ Action Required: You need to populate SERVICE_TREE_ID in .env.dev
- ℹ️ Alternative: Use existing Azure AD app or create manually

## Next Steps
1. Choose one of the solutions above
2. Run `azd provision` again
3. The remaining steps should complete successfully

## Reference
- TSG Documentation: https://aka.ms/service-management-reference-error
- Service Tree Portal: https://servicetree.microsoft.com
