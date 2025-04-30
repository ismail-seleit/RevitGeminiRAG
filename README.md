# RevitGeminiRAG - Revit Plugin with RAG-based API Interaction

**Disclaimer:** This project is shared "as-is" as a proof-of-concept and is **not actively maintained**. It demonstrates a method for enabling Large Language Models (LLMs) like Google Gemini to interact with the Revit API using a Retrieval-Augmented Generation (RAG) system. Feel free to fork, modify, and build upon it, but expect potential bugs or limitations. Use at your own risk.

## Overview

RevitGeminiRAG is a Revit plugin that allows users to interact with their Revit models using natural language queries processed by Google's Gemini models. It leverages a RAG system to provide the LLM with relevant context from the Revit API documentation, enabling it to understand and respond to Revit-specific requests more effectively.

## How it Works (High-Level)

1.  **User Input:** The user enters a prompt via a dedicated Revit panel (implemented in `PromptForm.cs`).
2.  **RAG Processing:** A Python script (`python/generate_rag_prompt.py`) preprocesses the prompt and retrieves relevant Revit API information from a pre-built knowledge base.
3.  **LLM Interaction:** The original prompt and the retrieved context are sent to the Google Gemini API using the `Google.Cloud.AIPlatform.V1` library within the C# plugin.
4.  **Response Generation:** Gemini generates a response based on the prompt and the provided API context.
5.  **Display:** The plugin (`RunRAGCommand.cs`) receives the response and displays it to the user in the prompt window.

## RAG Knowledge Base (Revit API Context)

*   **Current Version:** The included RAG knowledge base is built upon the **Revit 2025 API documentation**. Therefore, the plugin's understanding is currently optimized for Revit 2025.
*   **Updating for Other Revit Versions:** To adapt this plugin for a different Revit version, you will need to rebuild the RAG knowledge base. The general process involves:
    1.  Downloading the Revit SDK for the target version from the Autodesk Developer Network (ADN).
    2.  Locating the API documentation, often provided as a Compiled HTML Help (`.chm`) file (e.g., `RevitAPI.chm`).
    3.  Extracting and cleaning the content from the `.chm` file, potentially converting it to Markdown (`.md`) or plain text format suitable for processing.
    4.  Indexing the cleaned documentation (e.g., splitting into meaningful chunks based on classes, methods, properties).
    5.  Generating vector embeddings for the indexed documentation using an appropriate embedding model (compatible with the retrieval mechanism in the Python script).
    6.  Storing these embeddings in a vector database or a file format that the `python/generate_rag_prompt.py` script can efficiently query. *Note: The specifics of steps 3-6 depend on the implementation within the Python script and the chosen embedding/vector store technologies.*

## Features

*   Natural language interaction with Revit models via a dedicated panel.
*   Integration with Google Gemini LLM for response generation.
*   Retrieval-Augmented Generation (RAG) system providing Revit 2025 API context to the LLM.

## Prerequisites

*   **Autodesk Revit 2025** (Required for full compatibility with the current RAG knowledge base)
*   **.NET Framework 4.8** (Standard for Revit 2025 plugins)
*   **Visual Studio** (e.g., 2019, 2022) with the ".NET desktop development" workload installed.
*   **Google Cloud Platform Account and Project:** You need an active GCP project to generate an API key.
*   **A valid Google API Key:** This is essential for authenticating requests to the Gemini API.
*   **(Optional) Python Environment:** If modifications to the `python/generate_rag_prompt.py` script or the RAG generation process are needed, a Python environment with relevant libraries (e.g., for embedding generation, vector stores) will be required.

## Setup and Installation

1.  **Clone the Repository:**
    ```bash
    git clone <your-repository-url>
    cd RevitGeminiRAG
    ```
2.  **Set Google API Key:**
    *   **CRITICAL:** You MUST set your Google API Key as a Windows Environment Variable for the plugin to authenticate with Google Cloud.
    *   Search for "Edit the system environment variables" in the Windows search bar and open it.
    *   Click the "Environment Variables..." button.
    *   Under "User variables" (or "System variables" if you prefer), click "New...".
    *   Variable name: `GOOGLE_API_KEY`
    *   Variable value: `<Your_Actual_Google_API_Key>`
    *   Click OK on all dialogs.
    *   **Important:** Restart Visual Studio and/or Revit if they were open to ensure they pick up the new environment variable.
3.  **Build the Project:**
    *   Open `RevitGeminiRAG.sln` in Visual Studio.
    *   **Check References:** Verify that the Revit API DLLs (`RevitAPI.dll`, `RevitAPIUI.dll`) are correctly referenced. By default, the `.csproj` might point to a standard Revit installation path (e.g., `C:\Program Files\Autodesk\Revit 2025\`). If your installation is different, you'll need to update the reference paths in Visual Studio (Solution Explorer -> References -> Right-click -> Add Reference -> Browse).
    *   **Restore NuGet Packages:** Right-click the solution in Solution Explorer and select "Restore NuGet Packages".
    *   **Build:** Build the solution (Build > Build Solution). This will compile the code and create the necessary DLLs in the `bin\Debug\` or `bin\Release\` folder (relative to the project directory).
4.  **Install the Plugin in Revit:**
    *   **Locate Add-ins Folder:** Navigate to your Revit Add-ins folder for the current user:
        `%APPDATA%\Autodesk\Revit\Addins\2025`
        *(Create the `2025` folder if it doesn't exist)*
    *   **Copy Addin Manifest:** Copy the `RevitGeminiRAG.addin` file from the root of your cloned repository into this `2025` folder.
    *   **Edit Addin Manifest:** Open the copied `RevitGeminiRAG.addin` file in a text editor (like Notepad).
    *   **Update Assembly Path:** Modify the `<Assembly>` tag to point to the **full path** of the `RevitGeminiRAG.dll` file that was generated when you built the project. For example:
        `<Assembly>C:\path\to\your\cloned\repo\RevitGeminiRAG\bin\Debug\RevitGeminiRAG.dll</Assembly>`
        *(Replace `C:\path\to\your\cloned\repo\` with the actual path where you cloned the repository and ensure you point to the correct build output folder, e.g., `bin\Debug` or `bin\Release`)*.
    *   **Copy Dependencies:** Ensure that all necessary dependency DLLs (like the Google Cloud libraries from the build output folder) are located either in the same directory as `RevitGeminiRAG.dll` or in a location where Revit can find them. Copying them to the same folder as the main DLL is usually the simplest approach.

## Usage

1.  Start Autodesk Revit 2025.
2.  If the plugin installed correctly, navigate to the "Add-Ins" tab on the Revit ribbon.
3.  You should find a button related to "RevitGeminiRAG" (likely named based on the `RunRAGCommand` class). Click it.
4.  A window (`PromptForm`) will appear. Enter your natural language query about the Revit model or API into the text box.
5.  Click the submit/run button.
6.  The plugin will communicate with the Gemini API (using your API key and the RAG context) and display the generated response in the window.

## Dependencies (Key NuGet Packages)

*   Google.Cloud.AIPlatform.V1
*   Google.Apis / Google.Apis.Auth / Google.Apis.Core
*   Grpc.* (Core, Net.Client, Auth, etc.)
*   Newtonsoft.Json
*   (Review `packages.config` or the `.csproj` file for a complete list)

## License

This project is licensed under the [MIT License](LICENSE).
