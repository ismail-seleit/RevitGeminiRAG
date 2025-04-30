using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks; // Needed for Task.Run potentially, although we use .Result here

// --- IMPORTANT ---
// Ensure the helper methods in RunRAGCommand are accessible (e.g., public or internal)
// Ensure the namespace matches your project structure if different from RevitGeminiRAG
namespace RevitGeminiRAG
{
    [Transaction(TransactionMode.Manual)] // Test command still needs transaction mode
    [Regeneration(RegenerationOption.Manual)]
    public class StressTestRAGCommand : IExternalCommand
    {
        // --- Test Configuration ---
        private const string LogFileName = "RevitGeminiRAG_StressTestLog.txt";
        private static readonly List<string> TestPrompts = new List<string>
        {
             // --- Add your 15+ test prompts here ---
            // --- Basic Selection & Identification ---
            "select all curtain wall elements in the project",
            "count the total number of mullions in the active view",
            "select all panels of type 'System Panel: Glazed' within selected curtain walls",
            "identify and select all curtain grids (horizontal and vertical) in the currently selected curtain wall",
            "list the Element IDs of all mullion types currently used in the project",

            // --- Simple Modifications & Type Swapping ---
            "change all selected curtain wall panels to the 'Solid Panel' type",
            "replace all mullions of type 'Rectangular Mullion 50x150mm' with 'Circular Mullion 100mm'",
            "pin all mullions in the active view",
            "add a grid segment to the currently selected curtain grid line",
            "remove mullion segments along the selected curtain grid line",

            // --- Basic Parameter Updates ---
            "set the 'Offset' parameter to 100mm for all selected vertical curtain grids",
            "update the 'Angle' parameter to 45 degrees for all selected corner mullions",
            "set the 'Mark' parameter to 'CP-01', 'CP-02', etc., sequentially for all selected curtain panels",
            "add 'Exterior Use' to the 'Comments' parameter for all mullions hosted on curtain walls named 'Facade_*'",
            "change the 'Material' parameter for selected panel types to 'Aluminum Composite Material'",

            // --- Conditional Modifications ---
            "unpin all interior curtain grid lines, leaving perimeter grids pinned",
            "change the type of any curtain panel with an area less than 0.5 square meters to 'Spandrel Panel - Dark'",
            "set a custom parameter 'Is Horizontal Mullion' to true for all mullions connected to horizontal grid lines",
            "delete all mullions associated with grid lines that have an offset value of 0",
            "if a curtain wall's 'Function' parameter is 'Interior', change all its panels to 'System Panel: Glazed - Single Pane'",

            // --- Geometric Adjustments & Layout ---
            "adjust the selected vertical curtain grid layout to have a fixed number of 5 evenly spaced grids",
            "modify the selected horizontal curtain grid layout to use 'Fixed Distance' spacing of 1200mm, starting from the bottom",
            "align the vertical grid lines of the selected curtain wall with the nearest structural grid lines",
            "remove all existing horizontal grid lines from the selected curtain wall and add one at exactly 1000mm from the base",
            "change the join condition for all selected intersecting mullions to 'Make Continuous - Vertical Dominant'",

            // --- Complex Selection & Filtering ---
            "select all curtain wall panels that are adjacent to a 'Corner Mullion' type",
            "identify and select mullions whose geometry directly intersects with any Floor element",
            "find and select all curtain grids that are not perfectly vertical or horizontal (i.e., angled grids)",
            "select all 'Glazed' panels on curtain walls facing exactly North (Project North)",
            "create a view filter to hide all mullions shorter than 300mm in the current view",

            // --- Advanced Parameter Logic & Updates ---
            "set a custom Yes/No parameter 'Is Corner Panel' to true for all panels located at the start or end of a curtain wall run",
            "calculate the length of each selected mullion and write it to a custom 'Mullion Length' parameter",
            "for selected angled curtain grids, calculate the angle relative to the host wall's direction and store it in a 'Grid Angle' parameter",
            "identify panels located within 500mm of the top edge of their host curtain wall and set their 'Comments' to 'Top Row Panel'",
            "update the profile shape of selected mullion types based on an imported profile family named 'Custom Mullion Profile'",

            // --- Rule-Based Creation & Deletion ---
            "add mullions of type 'Quad Corner 150mm' only at intersections of vertical and horizontal grids",
            "delete all curtain wall panels whose 'Type Name' parameter contains 'Empty'",
            "automatically add 'Rectangular Mullion 50x100mm' to all curtain grid lines that currently have no mullions assigned",
            "find and delete any curtain grid lines that are closer than 150mm to an adjacent parallel grid line",
            "create a new curtain wall using the 'Storefront' type, matching the footprint of a selected Room element",

            // --- Interaction with Other Elements & Systems ---
            "split selected curtain wall panels horizontally where they are intersected by Level planes",
            "adjust the height of selected curtain walls so their top edge aligns with the underside of the nearest Floor or Roof element above",
            "find curtain walls embedded within basic walls and ensure the 'Automatically Embed' property is checked",
            "modify the profile of a curtain wall to create an opening matching the geometry of a selected intersecting Duct element",
            "align the primary vertical grid of a selected curtain wall to the centerline of an adjacent structural column",

            // --- Complex View-Specific Operations ---
            "override the projection line color of mullions based on their 'Type Name' (e.g., 'Corner'=Red, 'Rectangular'=Blue) in the active view",
            "apply transparency override to curtain panels based on their 'Material' parameter (e.g., 'Glass'=70%, 'Spandrel'=0%)",
            "hide all curtain wall grids (but not mullions or panels) in all 3D views whose names start with 'Presentation_'",
            "color curtain panels in the current elevation view based on their distance from the ground level (e.g., gradient from blue to red)",
            "create and apply a view filter that isolates only the 'Corner Mullion' types and displays them with a heavy dashed line style",        };

        // --- Internal Constants (Mirroring RunRAGCommand for loop logic) ---
        private const int MaxRetryAttempts = 5; // Match the original command

        private string _logFilePath;
        private StringBuilder _currentPromptLog;

        public Result Execute(
            ExternalCommandData commandData,
            ref string message,
            ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;
            UIDocument uidoc = uiapp.ActiveUIDocument;
            Document doc = uidoc.Document;

            // --- Setup Logging ---
            try
            {
                string documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                _logFilePath = Path.Combine(documentsPath, LogFileName);
                File.AppendAllText(_logFilePath, $"--- Stress Test Run Started: {DateTime.Now} ---\n");
            }
            catch (Exception ex)
            {
                message = $"Failed to initialize log file '{_logFilePath}': {ex.Message}";
                TaskDialog.Show("Logging Error", message);
                return Result.Failed;
            }

            Log("Stress test initiated.");
            Log($"Found {TestPrompts.Count} prompts to test.");

            int promptsSucceeded = 0;
            int promptsFailed = 0;
            int promptsCancelled = 0;

            RunRAGCommand ragInstance = new RunRAGCommand();

            // --- Loop Through Test Prompts ---
            for (int i = 0; i < TestPrompts.Count; i++)
            {
                string userPrompt = TestPrompts[i];
                _currentPromptLog = new StringBuilder();
                LogPrompt($"\n--- Testing Prompt {i + 1}/{TestPrompts.Count}: [{userPrompt}] ---");

                string initialRagPrompt = string.Empty;
                bool overallSuccess = false;
                string lastErrorMessage = string.Empty;
                string lastScriptOutput = string.Empty;
                string currentCodeToExecute = string.Empty;
                string lastFailedCode = string.Empty;
                string finalCodeToShowUser = string.Empty;

                Stopwatch promptStopwatch = Stopwatch.StartNew();

                try
                {
                    // --- Step 2: Generate *Initial* LLM Prompt (via RAG) ---
                    LogPrompt("Step 2: Generating initial RAG prompt...");
                    Stopwatch ragGenWatch = Stopwatch.StartNew();
                    try
                    {
                        initialRagPrompt = ragInstance.GenerateLlmPromptViaPython(userQuery: userPrompt);
                        ragGenWatch.Stop();

                        if (string.IsNullOrWhiteSpace(initialRagPrompt))
                        {
                            throw new InvalidOperationException("Python RAG script returned empty prompt.");
                        }
                        LogPrompt($"Step 2: RAG prompt generation successful ({ragGenWatch.ElapsedMilliseconds}ms). Length: {initialRagPrompt.Length}");

                        // ADDED LOGGING: Log the actual initial RAG prompt
                        LogPrompt("--- Initial RAG Prompt ---");
                        LogPrompt(initialRagPrompt); // Log the full prompt
                        LogPrompt("--- End Initial RAG Prompt ---");
                    }
                    catch (Exception ex)
                    {
                        ragGenWatch.Stop();
                        lastErrorMessage = $"Error running Python RAG script: {ex.Message}{(ex.InnerException != null ? $"\nInner Exception: {ex.InnerException.Message}" : "")}\nCheck Debug Output/Python Logs for details.";
                        LogPrompt($"Step 2: RAG prompt generation FAILED ({ragGenWatch.ElapsedMilliseconds}ms). Error: {lastErrorMessage}");
                        promptsFailed++;
                        promptStopwatch.Stop();
                        LogPrompt($"--- Prompt {i + 1} FAILED (RAG Generation Error) in {promptStopwatch.ElapsedMilliseconds}ms ---\n");
                        File.AppendAllText(_logFilePath, _currentPromptLog.ToString());
                        continue;
                    }

                    // --- Step 3 & 4: Feedback Loop (Call API, Execute Code, Retry on Error) ---
                    LogPrompt($"Step 3/4: Starting API call and execution loop (Max Attempts: {MaxRetryAttempts}).");

                    for (int attempt = 1; attempt <= MaxRetryAttempts; attempt++)
                    {
                        LogPrompt($"--- Attempt {attempt}/{MaxRetryAttempts} ---");
                        string promptToSend = string.Empty;
                        string apiResponse = string.Empty;
                        currentCodeToExecute = string.Empty;
                        bool currentAttemptExecutionSuccess = false;
                        string scriptOutput = string.Empty;
                        string scriptError = string.Empty;

                        // 3a. Determine the prompt for this attempt
                        if (attempt == 1)
                        {
                            promptToSend = initialRagPrompt;
                            LogPrompt($"   3a: Using initial RAG prompt.");
                            // Initial RAG prompt already logged outside the loop
                        }
                        else
                        {
                            LogPrompt($"   3a: Constructing 'fix-it' prompt.");
                            promptToSend = ragInstance.ConstructFixItPrompt(userPrompt, lastFailedCode, lastErrorMessage);
                            if (string.IsNullOrWhiteSpace(promptToSend))
                            {
                                lastErrorMessage = "Failed to construct a valid 'fix-it' prompt. Aborting retries.";
                                LogPrompt($"   3a: FAILED to construct fix-it prompt.");
                                break;
                            }
                            // ADDED LOGGING: Log the fix-it prompt being sent
                            LogPrompt($"--- Fix-it Prompt (Attempt {attempt}) ---");
                            LogPrompt(promptToSend); // Log the full fix-it prompt
                            LogPrompt($"--- End Fix-it Prompt (Attempt {attempt}) ---");
                        }

                        // 3b. Call Gemini API
                        LogPrompt($"   3b: Calling Gemini API...");
                        Stopwatch apiWatch = Stopwatch.StartNew();
                        try
                        {
                            apiResponse = ragInstance.CallGeminiApiAsync(promptToSend).Result;
                            apiWatch.Stop();

                            if (string.IsNullOrWhiteSpace(apiResponse))
                            {
                                throw new InvalidOperationException("API returned an empty or null response body.");
                            }

                            string extractedContent = ragInstance.ParseGeminiResponse(apiResponse);
                            if (string.IsNullOrWhiteSpace(extractedContent))
                            {
                                throw new InvalidOperationException("Failed to extract valid content from Gemini response JSON.");
                            }

                            string extractedCode = RunRAGCommand.ExtractPythonCode(extractedContent);
                            currentCodeToExecute = !string.IsNullOrWhiteSpace(extractedCode) ? extractedCode : extractedContent;
                            finalCodeToShowUser = currentCodeToExecute;

                            if (string.IsNullOrWhiteSpace(extractedCode))
                            {
                                LogPrompt("      WARNING: Could not extract fenced Python code. Using full extracted content.");
                            }
                            LogPrompt($"   3b: Gemini API call & parsing successful ({apiWatch.ElapsedMilliseconds}ms). Code length: {currentCodeToExecute.Length}");

                            // ADDED LOGGING: Log the generated Python code before execution
                            LogPrompt($"--- Generated Python Code (Attempt {attempt}) ---");
                            LogPrompt("```python"); // Use markdown fence for readability
                            LogPrompt(currentCodeToExecute); // Log the full code
                            LogPrompt("```");
                            LogPrompt($"--- End Generated Python Code (Attempt {attempt}) ---");

                        }
                        catch (AggregateException aggEx)
                        {
                            apiWatch.Stop();
                            Exception relevantEx = aggEx.InnerExceptions.FirstOrDefault() ?? aggEx;
                            lastErrorMessage = $"Attempt {attempt}: Error calling Gemini API or processing response: {relevantEx.Message}";
                            if (relevantEx is System.Net.Http.HttpRequestException httpEx)
                            {
                                if (httpEx.Data.Contains("ResponseBody")) lastErrorMessage += $"\nResponse Body: {httpEx.Data["ResponseBody"]}";
                                if (httpEx.Data.Contains("StatusCode")) lastErrorMessage += $"\nStatus Code: {httpEx.Data["StatusCode"]}";
                            }
                            lastFailedCode = string.IsNullOrEmpty(currentCodeToExecute) ? lastFailedCode : currentCodeToExecute;
                            finalCodeToShowUser = lastFailedCode;
                            LogPrompt($"   3b: Gemini API call or processing FAILED ({apiWatch.ElapsedMilliseconds}ms). Error: {lastErrorMessage}");
                            continue;
                        }
                        catch (Exception apiEx)
                        {
                            apiWatch.Stop();
                            lastErrorMessage = $"Attempt {attempt}: Error calling Gemini API or processing response: {apiEx.Message}";
                            if (apiEx is System.Net.Http.HttpRequestException httpEx)
                            {
                                if (httpEx.Data.Contains("ResponseBody")) lastErrorMessage += $"\nResponse Body: {httpEx.Data["ResponseBody"]}";
                                if (httpEx.Data.Contains("StatusCode")) lastErrorMessage += $"\nStatus Code: {httpEx.Data["StatusCode"]}";
                            }
                            lastFailedCode = string.IsNullOrEmpty(currentCodeToExecute) ? lastFailedCode : currentCodeToExecute;
                            finalCodeToShowUser = lastFailedCode;
                            LogPrompt($"   3b: Gemini API call or processing FAILED ({apiWatch.ElapsedMilliseconds}ms). Error: {apiEx}");
                            continue;
                        }

                        // 4a. Execute the code (AUTOMATICALLY, NO REVIEW DIALOG IN TEST)
                        LogPrompt($"   4a: Attempting to execute generated code (Attempt {attempt})...");
                        Stopwatch execWatch = Stopwatch.StartNew();
                        using (Transaction pyTransaction = new Transaction(doc, $"Stress Test Python Code Attempt {attempt} for Prompt {i + 1}"))
                        {
                            try
                            {
                                pyTransaction.Start();
                                currentAttemptExecutionSuccess = ragInstance.ExecuteGeneratedPythonCode(
                                    currentCodeToExecute, doc, uidoc, uiapp, out scriptOutput, out scriptError);
                                execWatch.Stop();
                                lastScriptOutput = scriptOutput;

                                if (currentAttemptExecutionSuccess)
                                {
                                    pyTransaction.Commit();
                                    LogPrompt($"   4a: Code execution SUCCEEDED ({execWatch.ElapsedMilliseconds}ms). Transaction committed.");
                                    LogPrompt($"      Output:\n{scriptOutput}\n---");
                                    // --- START: Added code to save successful script ---
                                    try
                                    {
                                        // Get plugin directory (assuming StressTest is in the same assembly/location)
                                        string assemblyLocation = System.Reflection.Assembly.GetExecutingAssembly().Location;
                                        string pluginDirectory = System.IO.Path.GetDirectoryName(assemblyLocation);
                                        // Use the same folder name
                                        string saveFolderName = "GeneratedSuccessfulCode";
                                        string saveFolderPath = System.IO.Path.Combine(pluginDirectory, saveFolderName);

                                        // Ensure the target directory exists
                                        System.IO.Directory.CreateDirectory(saveFolderPath);

                                        // --- Generate filename from user prompt ---
                                        string sanitizedPromptPart = "UserRequest"; // Default prefix
                                        int maxPromptLength = 60; // Max characters from prompt in filename

                                        // userPrompt variable is available from the outer loop
                                        if (!string.IsNullOrWhiteSpace(userPrompt))
                                        {
                                            // Get invalid chars
                                            char[] invalidChars = System.IO.Path.GetInvalidFileNameChars();

                                            // Create a clean string by replacing invalid chars with '_'
                                            var cleanedChars = userPrompt.Select(ch => invalidChars.Contains(ch) ? '_' : ch);
                                            string cleanedString = new string(cleanedChars.ToArray()).Trim('_', ' ');

                                            // Replace multiple consecutive underscores/spaces with a single underscore
                                            cleanedString = System.Text.RegularExpressions.Regex.Replace(cleanedString, @"[\s_]+", "_");

                                            // Truncate if too long
                                            if (cleanedString.Length > maxPromptLength)
                                            {
                                                cleanedString = cleanedString.Substring(0, maxPromptLength).TrimEnd('_');
                                            }

                                            if (!string.IsNullOrWhiteSpace(cleanedString))
                                            {
                                                sanitizedPromptPart = cleanedString;
                                            }
                                        }
                                        // --- End filename generation ---

                                        // Create a unique filename (using sanitized prompt + timestamp)
                                        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                                        string fileName = $"{sanitizedPromptPart}_{timestamp}.py";
                                        string filePath = System.IO.Path.Combine(saveFolderPath, fileName);

                                        // Write the successfully executed code (currentCodeToExecute) to the file
                                        // Use UTF8 encoding to be safe
                                        System.IO.File.WriteAllText(filePath, currentCodeToExecute, System.Text.Encoding.UTF8);

                                        // Log the save action using the stress test logger
                                        LogPrompt($"      -> Successfully saved generated code to: {filePath}");
                                    }
                                    catch (Exception saveEx)
                                    {
                                        // Log the error using the stress test logger, but don't fail the prompt
                                        LogPrompt($"      WARNING: Failed to save the successful script. Error: {saveEx.Message}");
                                    }
                                    // --- END: Added code to save successful script ---
                                    overallSuccess = true;
                                }
                                else
                                {
                                    pyTransaction.RollBack();
                                    lastErrorMessage = scriptError;
                                    lastFailedCode = currentCodeToExecute;
                                    LogPrompt($"   4a: Code execution FAILED ({execWatch.ElapsedMilliseconds}ms). Transaction rolled back.");
                                    LogPrompt($"      Error:\n{scriptError}\n---");
                                    LogPrompt($"      Output (if any):\n{scriptOutput}\n---");
                                }
                            }
                            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                            {
                                execWatch.Stop();
                                LogPrompt($"   4a: Transaction cancelled by Revit/User during execution ({execWatch.ElapsedMilliseconds}ms).");
                                if (pyTransaction.GetStatus() == TransactionStatus.Started) { try { pyTransaction.RollBack(); } catch { } }
                                lastErrorMessage = "Python code execution cancelled by Revit/user during operation.";
                                overallSuccess = false;
                                promptsCancelled++;
                                goto EndPromptProcessing;
                            }
                            catch (Exception ex)
                            {
                                execWatch.Stop();
                                LogPrompt($"   4a: CRITICAL error during Python execution/transaction ({execWatch.ElapsedMilliseconds}ms): {ex}");
                                if (pyTransaction.GetStatus() == TransactionStatus.Started) { try { pyTransaction.RollBack(); } catch { } }
                                lastErrorMessage = $"Attempt {attempt}: Critical error executing Python code wrapper: {ex.Message}";
                                lastFailedCode = currentCodeToExecute;
                                overallSuccess = false;
                                promptsFailed++;
                                goto EndPromptProcessing;
                            }
                        }
                        if (overallSuccess) break;
                    } // End For Loop (Attempts)
                }
                catch (Exception outerEx)
                {
                    lastErrorMessage = $"Unexpected error processing prompt: {outerEx}";
                    LogPrompt($"--- CRITICAL UNEXPECTED ERROR for Prompt {i + 1}: {outerEx} ---");
                    overallSuccess = false;
                }

            EndPromptProcessing:;

                // --- Step 5: Final Logging for this Prompt ---
                promptStopwatch.Stop();
                LogPrompt($"--- Prompt {i + 1} Final Status ---");
                if (overallSuccess)
                {
                    promptsSucceeded++;
                    LogPrompt($"Result: SUCCEEDED");
                    LogPrompt($"Final Output:\n{lastScriptOutput}\n---");
                }
                else
                {
                    if (lastErrorMessage.ToLowerInvariant().Contains("cancel"))
                    {
                        promptsCancelled++;
                        LogPrompt($"Result: CANCELLED/ABORTED");
                    }
                    else
                    {
                        promptsFailed++;
                        LogPrompt($"Result: FAILED after {MaxRetryAttempts} attempts or due to critical error.");
                    }
                    LogPrompt($"Last Error Message:\n{lastErrorMessage}\n---");
                    // Log the final generated code again in case of failure for easier debugging
                    LogPrompt($"Last Code Attempted/Generated:\n```python\n{finalCodeToShowUser}\n```\n---");
                    LogPrompt($"Last Script Output (before final error, if any):\n{lastScriptOutput}\n---");
                }
                LogPrompt($"Total time for prompt: {promptStopwatch.ElapsedMilliseconds}ms");
                LogPrompt($"--- End Prompt {i + 1} ---");

                File.AppendAllText(_logFilePath, _currentPromptLog.ToString());

            } // End For Loop (Prompts)

            // --- Final Summary ---
            string summary = $"\n--- Stress Test Summary ---\n" +
                             $"Total Prompts Tested: {TestPrompts.Count}\n" +
                             $"Succeeded: {promptsSucceeded}\n" +
                             $"Failed: {promptsFailed}\n" +
                             $"Cancelled/Aborted: {promptsCancelled}\n" +
                             $"Log file saved to: {_logFilePath}\n" +
                             $"--- Stress Test Run Finished: {DateTime.Now} ---\n";
            Log(summary); // Log to debug output and append to file one last time
            // File.AppendAllText(_logFilePath, summary); // Already appended via Log() helper

            TaskDialog.Show("Stress Test Complete",
                $"Stress Test Finished.\n\n" +
                $"Total Prompts: {TestPrompts.Count}\n" +
                $"Succeeded: {promptsSucceeded}\n" +
                $"Failed: {promptsFailed}\n" +
                $"Cancelled/Aborted: {promptsCancelled}\n\n" +
                $"Detailed log saved to:\n{_logFilePath}");

            return Result.Succeeded;
        }

        // Helper method to log to both Debug output and the current prompt's log string
        private void LogPrompt(string message)
        {
            // Simple way to handle potential multi-line messages for file logging
            string timestampedMessage = $"{DateTime.Now:HH:mm:ss.fff} | ";
            string indentedMessage = message.Replace("\n", $"\n{timestampedMessage}  "); // Indent subsequent lines
            Debug.WriteLine($"{timestampedMessage}{indentedMessage}"); // Write to VS Debug Output
            _currentPromptLog?.AppendLine($"{timestampedMessage}{indentedMessage}"); // Append to in-memory log
        }

        // Helper method to log general messages directly to the file (e.g., start/end)
        private void Log(string message)
        {
            string timestampedMessage = $"{DateTime.Now:HH:mm:ss.fff} | {message}";
            Debug.WriteLine(timestampedMessage); // Write to VS Debug Output
            try
            {
                File.AppendAllText(_logFilePath, timestampedMessage + "\n");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ERROR: Failed to write general log message to file: {ex.Message}");
            }
        }
    }
}