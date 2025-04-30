using Autodesk.Revit.UI;
using System;
using System.Reflection; // Needed for finding assembly location
// using System.Windows.Media.Imaging; // Uncomment if adding icons later
// using System.IO; // Might need this if loading icons from files

namespace RevitGeminiRAG
{
    public class App : IExternalApplication
    {
        // Static variable to store the path to this assembly
        static string AddInPath = typeof(App).Assembly.Location;

        public Result OnStartup(UIControlledApplication application)
        {
            // --- Ribbon Tab Configuration ---
            string tabName = "Gemini RAG";

            try // Wrap all UI creation in a try-catch
            {
                // 1. Create Ribbon Tab (or ensure it exists)
                try
                {
                    application.CreateRibbonTab(tabName);
                }
                catch (Autodesk.Revit.Exceptions.ArgumentException)
                {
                    // Tab already exists - ignore exception
                }
                catch (Exception ex)
                {
                    // Log or show other tab creation errors if needed
                    System.Diagnostics.Debug.WriteLine($"Error trying to create ribbon tab '{tabName}': {ex.Message}");
                    // Optionally re-throw or return Failed if tab is essential
                }

                // 2. Create Ribbon Panel
                // If the panel might already exist from another add-in, you might want to search first
                // For simplicity, we assume we are creating it here.
                string panelName = "Commands";
                RibbonPanel ribbonPanel = application.CreateRibbonPanel(tabName, panelName);


                // --- Create Button for the Original RunRAGCommand ---

                string runRagCommandClass = typeof(RunRAGCommand).FullName; // Get the full class name

                PushButtonData runRagButtonData = new PushButtonData(
                    "RunRAGButton",           // Internal name (unique within panel)
                    "Run Gemini\nRAG",        // Text displayed on the button (\n creates a line break)
                    AddInPath,                // Path to the assembly containing the command
                    runRagCommandClass        // Full class name of the command
                );
                runRagButtonData.ToolTip = "Generates and runs Python code via Gemini RAG workflow.";
                // runRagButtonData.LargeImage = ... // Optional Icon

                // Add the first button
                PushButton runRagPushButton = ribbonPanel.AddItem(runRagButtonData) as PushButton;


                // --- Create Button for the StressTestRAGCommand ---

                string stressTestCommandClass = typeof(StressTestRAGCommand).FullName; // Get the full class name

                PushButtonData stressTestButtonData = new PushButtonData(
                    "StressTestRAGButton",    // Internal name (unique within panel)
                    "Stress Test\nRAG",       // Text displayed on the button
                    AddInPath,                // Path to the assembly
                    stressTestCommandClass    // Full class name of the stress test command
                );
                stressTestButtonData.ToolTip = "Runs multiple predefined prompts through the Gemini RAG command for testing.";
                // stressTestButtonData.LargeImage = ... // Optional Icon (use a different one?)

                // Add the second button to the SAME panel
                PushButton stressTestButton = ribbonPanel.AddItem(stressTestButtonData) as PushButton;


                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                // Show errors if UI creation fails
                TaskDialog.Show("Error Setting Up Gemini RAG Ribbon", $"Failed to create buttons: {ex.Message}\n{ex.StackTrace}");
                return Result.Failed;
            }
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            // Clean up resources if needed
            return Result.Succeeded;
        }

        // --- Optional: Helper method to load embedded icons ---
        /*
        private System.Windows.Media.ImageSource GetEmbeddedImage(string resourceName)
        {
            // ... (implementation from previous examples) ...
        }
        */
    }
}