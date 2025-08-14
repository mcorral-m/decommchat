# Cluster Decommissioning Agent

A comprehensive intelligent agent system for identifying, analyzing, and managing Azure cluster decommissioning opportunities. This system leverages AI, Kusto data analysis, and intelligent scoring to identify underutilized clusters and assist in the decommissioning process.

![Azure Decommission Agent](https://microsoft.github.io/images/azure.png)

## Table of Contents
- [Overview](#overview)
- [Key Features](#key-features)
- [System Architecture](#system-architecture)
- [Installation](#installation)
- [Configuration](#configuration)
- [Usage](#usage)
- [Development](#development)
- [Plugin System](#plugin-system)
- [Data Providers](#data-providers)
- [Contributing](#contributing)
- [License](#license)

## Overview

The Cluster Decommissioning Agent is an AI-powered assistant designed to help Azure operations teams identify underutilized clusters that are candidates for decommissioning. By analyzing cluster data from Kusto databases (AzureDCM and OneCapacity), the system applies sophisticated eligibility rules, scoring algorithms, and filtering capabilities to provide actionable recommendations.

This agent integrates with Microsoft Teams and can be deployed as an Azure Bot Service, enabling seamless interaction with operations teams through natural language.

## Key Features

- **Intelligent Cluster Analysis**: Analyzes cluster data using comprehensive metrics
- **Eligibility Engine**: Applies configurable rules to determine decommission candidates
- **Scoring System**: Rates clusters based on multiple factors including age, utilization, and cost
- **Interactive Filtering**: Dynamic filtering capabilities for cluster data exploration
- **Adaptive Card UI**: Rich visual presentations for cluster data and recommendations
- **Integration with Azure DCM and OneCapacity**: Seamless data retrieval from Azure monitoring systems
- **Microsoft Teams Integration**: Deploy as a bot in Microsoft Teams for easy access

## System Architecture

The system follows a modular architecture with the following components:

- **Core Bot Logic**: Built with Microsoft's Bot Framework and Semantic Kernel
- **Data Providers**: Abstracted interfaces for accessing cluster data
- **Domain Services**: 
  - `ClusterEligibilityEngine`: Determines eligible clusters for decommissioning
  - `FilterService`: Provides advanced filtering capabilities
  - `ScoringService`: Scores clusters based on multiple factors
- **Plugins**:
  - `ScoringPlugin`: Exposes scoring capabilities to the bot
  - `EligibilityPlugin`: Manages eligibility rules and processing
  - `ClusterFilteringPlugin`: Handles advanced filtering operations
  - `AdaptiveCardPlugin`: Generates rich visual presentations

## Installation

### Prerequisites

- .NET 9.0 SDK or later
- Azure subscription with access to:
  - Azure Bot Service
  - Azure OpenAI Service
  - Kusto/ADX clusters (Azure DCM and OneCapacity)
- Visual Studio 2022 or later / VS Code with C# extensions

### Setup

1. Clone the repository:
```bash
git clone 
```

2. Navigate to the project directory:
```bash
cd MyM365AgentDecommision
```

3. Restore dependencies:
```bash
dotnet restore
```

4. Build the project:
```bash
dotnet build
```

## Configuration

Configuration is managed through appsettings files:

- `appsettings.json`: Base configuration
- `appsettings.Development.json`: Development environment overrides
- `appsettings.Local.json`: Local development settings
- `appsettings.Playground.json`: Testing environment settings

### Key Configuration Sections

```json
{
  "Kusto": {
    "ClusterUri": "https://azuredcm.kusto.windows.net",
    "DatabaseName": "AzureDCMDb",
    "UseUserPromptAuth": "false",
    "TimeoutSeconds": "300"
  },
  "OneCapacityKusto": {
    "ClusterUri": "https://onecapacityfollower.centralus.kusto.windows.net",
    "DatabaseName": "Shared"
  },
  "Azure": {
    "OpenAIEndpoint": "your-openai-endpoint",
    "OpenAIApiKey": "your-openai-key",
    "OpenAIDeploymentName": "your-deployment-name"
  }
}
```

## Usage

### Running Locally

To run the agent locally:

```bash
dotnet run
```

The agent will be available at `http://localhost:5000`.

### Teams Integration

For Teams integration, deploy using the Azure DevOps pipeline or manually:

1. Deploy the bot to Azure
2. Register the bot with Bot Framework
3. Install the Teams app using the app package in the `M365Agent/appPackage` directory

## Development

### Project Structure

```
MyM365AgentDecommision/
├── src/
│   ├── Agents/                # Agent implementation
│   ├── Bot/                   # Bot Framework integration
│   ├── Domain/                # Domain models
│   ├── Infrastructure/        # Data access and external services
│   ├── Plugin/                # Semantic Kernel plugins
│   ├── Queries/               # KQL queries
│   └── Services/              # Core business logic services
├── Tests/                     # Unit and integration tests
├── M365Agent/                 # Teams app package
├── Controllers/               # API controllers
└── docs/                      # Documentation
```

### Key Components

#### Data Flow

1. **Data Retrieval**: `KustoSdkDataProvider` retrieves cluster data from Azure DCM and OneCapacity
2. **Eligibility Processing**: `ClusterEligibilityEngine` determines which clusters are eligible for decommissioning
3. **Scoring**: `ScoringService` applies various metrics to rate clusters
4. **Filtering**: `ClusterFilterEngine` provides query capabilities for exploring the dataset
5. **Presentation**: `AdaptiveCardPlugin` generates visual representations for Teams

## Plugin System

The agent uses Microsoft Semantic Kernel for plugin architecture:

### ScoringPlugin

Provides capabilities to score and rank clusters based on configurable metrics:
- Age-based scoring
- Utilization-based scoring
- Cost-based scoring
- Combined scoring with weighted factors

### EligibilityPlugin

Manages the rules that determine which clusters are eligible for decommissioning:
- Age thresholds
- Utilization thresholds
- Region-based eligibility
- Custom rule creation and application

### ClusterFilteringPlugin

Enables advanced filtering capabilities:
- String matching and exclusion
- Numeric range filtering
- Multi-criteria filtering
- Sorting and pagination

### AdaptiveCardPlugin

Generates rich visual presentations for Teams:
- Cluster summary cards
- Detailed cluster information
- Recommendation cards
- Action cards for user interaction

## Data Providers

### KustoSdkDataProvider

The primary data provider that retrieves cluster information from:
- Azure DCM: Contains core cluster metrics and metadata
- OneCapacity: Contains additional capacity and utilization data

Supports:
- Authentication via Managed Identity or User Auth
- Query execution with timeout handling
- Data merging and normalization


Copyright (c) Microsoft Corporation. All rights reserved.

Licensed under the MIT License.
