// --- Using Statements ---
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Windows.Forms; // For SaveFileDialog, DialogResult, NativeWindow, Clipboard etc.
using System.Diagnostics;
using System.IO; // Explicitly for File, Directory (will qualify Path)
using System.Text; // For Encoding
using System.Reflection;
using System.Threading; // For Thread, ApartmentState
using System.Threading.Tasks;
using System.Net.Http;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using IronPython.Hosting;
using Microsoft.Scripting.Hosting;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Scripting; // Required for SourceCodeKind
using System.Data; // For DataTable and Excel export support

// --- Assembly Binding Redirect Note ---
// (Keep the note as is)

namespace RevitGeminiRAG
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class RunRAGCommand : IExternalCommand
    {
        // --- CONFIGURATION ---
        private const string GeminiModelId = "gemini-2.5-pro-preview-03-25";
        private const string GoogleApiKeyEnvVariable = "GOOGLE_API_KEY";
        private const int MaxOutputTokens = 8192;
        private const int MaxRetryAttempts = 5;
        // --- END CONFIGURATION ---

        // --- ADDED: Export Marker Prefix ---
        private const string ExportMarkerPrefix = "EXPORT::";
        // --- END ADDED ---

        public Result Execute(
          ExternalCommandData commandData,
          ref string message,
          ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;
            UIDocument uidoc = uiapp.ActiveUIDocument;
            Document doc = uidoc.Document;
            string userPrompt = string.Empty;
            string initialRagPrompt = string.Empty;

            // --- Step 1: Get User Prompt ---
            try
            {
                using (Gemini_RAG promptForm = new Gemini_RAG())
                {
                    try
                    {
                        NativeWindow revitWindow = new NativeWindow();
                        revitWindow.AssignHandle(uiapp.MainWindowHandle);
                        promptForm.ShowDialog(revitWindow);

                        if (promptForm.DialogResult == DialogResult.OK)
                        {
                            userPrompt = promptForm.UserPrompt;
                        }
                        else
                        {
                            return Result.Cancelled;
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Warning: Could not set owner window or show dialog modally: {ex.Message}");
                        if (promptForm.ShowDialog() == DialogResult.OK)
                        {
                            userPrompt = promptForm.UserPrompt;
                        }
                        else
                        {
                            return Result.Cancelled;
                        }
                    }

                    if (string.IsNullOrWhiteSpace(userPrompt))
                    {
                        TaskDialog.Show("Input Error", "Prompt cannot be empty.");
                        return Result.Failed;
                    }
                }
            }
            catch (Exception ex)
            {
                message = $"Error displaying prompt form: {ex.Message}";
                TaskDialog.Show("UI Error", message);
                Debug.WriteLine($"Prompt Form Exception: {ex}");
                return Result.Failed;
            }

            // --- Step 2: Generate *Initial* LLM Prompt (via RAG) ---
            try
            {
                Debug.WriteLine("Generating initial LLM prompt via Python RAG script...");
                initialRagPrompt = GenerateLlmPromptViaPython(userQuery: userPrompt);
                if (string.IsNullOrWhiteSpace(initialRagPrompt))
                {
                    message = "Failed to generate initial LLM prompt via Python script (returned empty). Check Python script logs/errors in Debug Output.";
                    TaskDialog.Show("Python Execution Error", message);
                    return Result.Failed;
                }
                Debug.WriteLine($"--- Initial RAG Prompt (Length: {initialRagPrompt.Length}) ---");
            }
            catch (Exception ex)
            {
                message = $"Error running Python RAG script: {ex.Message}";
                if (ex.InnerException != null) message += $"\nInner Exception: {ex.InnerException.Message}";
                message += "\n\nCheck Debug Output for detailed Python errors (PY_STDERR).";
                TaskDialog.Show("Python Execution Error", message);
                Debug.WriteLine($"Python Execution Exception: {ex}");
                return Result.Failed;
            }

            // --- Step 3 & 4: Feedback Loop (Call API, Execute Code, Retry on Error) ---
            bool overallSuccess = false;
            string lastErrorMessage = string.Empty;
            string lastScriptOutput = string.Empty;
            string currentCodeToExecute = string.Empty;
            string lastFailedCode = string.Empty;
            string finalCodeToShowUser = string.Empty;
            string finalSuccessMessage = "Python code executed successfully."; // Default message

            // Declare loop counter variable *here* if needed outside loop
            // int actualAttemptsMade = 0; // Example if needed (we don't strictly need it with the fix below)

            for (int attempt = 1; attempt <= MaxRetryAttempts; attempt++)
            {
                // actualAttemptsMade = attempt; // Example if tracking needed outside loop

                Debug.WriteLine($"--- Attempt {attempt} of {MaxRetryAttempts} ---");
                string promptToSend = string.Empty;
                string apiResponse = string.Empty;
                currentCodeToExecute = string.Empty; // Reset for this attempt

                // 3a. Determine the prompt
                if (attempt == 1)
                {
                    promptToSend = initialRagPrompt;
                    Debug.WriteLine("Using initial RAG prompt for first API call.");
                }
                else
                {
                    Debug.WriteLine("Constructing 'fix-it' prompt for retry.");
                    promptToSend = ConstructFixItPrompt(userPrompt, lastFailedCode, lastErrorMessage);
                    if (string.IsNullOrWhiteSpace(promptToSend))
                    {
                        message = "Failed to construct a valid 'fix-it' prompt. Aborting retries.";
                        Debug.WriteLine($"Error: {message}");
                        lastErrorMessage = message; // Store this specific error
                        break; // Exit the loop
                    }
                }

                // 3b. Call Gemini API
                try
                {
                    Debug.WriteLine($"Sending prompt to Gemini (Attempt {attempt})...");
                    apiResponse = CallGeminiApiAsync(promptToSend).Result; // Use .Result for simplicity here, consider async/await pattern throughout if preferred

                    if (string.IsNullOrWhiteSpace(apiResponse))
                    {
                        throw new InvalidOperationException("API returned an empty or null response body.");
                    }

                    Debug.WriteLine($"--- Raw Response Body from Gemini (Attempt {attempt}) ---");
                    // Debug.WriteLine(apiResponse); // Keep commented unless debugging response parsing
                    Debug.WriteLine($"--- End Raw Response Body (Attempt {attempt}) ---");

                    string extractedContent = ParseGeminiResponse(apiResponse);
                    if (string.IsNullOrWhiteSpace(extractedContent))
                    {
                        // Log specific reason if available from ParseGeminiResponse exception data
                        throw new InvalidOperationException("Failed to extract valid content from Gemini response (possibly due to safety filters or unexpected format). See Debug Output for details.");
                    }

                    currentCodeToExecute = ExtractPythonCode(extractedContent);
                    finalCodeToShowUser = currentCodeToExecute; // Update for potential display even if execution fails later
                    Debug.WriteLine($"--- Generated/Corrected Code (Attempt {attempt}) ---\n{currentCodeToExecute}\n--- End Code ---");
                }
                catch (AggregateException aggEx) // Handle async task exceptions from .Result
                {
                    Exception relevantEx = aggEx.InnerExceptions.FirstOrDefault() ?? aggEx;
                    Debug.WriteLine($"Gemini API Call or Processing Failed (Attempt {attempt}): {relevantEx}");
                    lastErrorMessage = $"Attempt {attempt}: Error calling Gemini API or processing its response: {relevantEx.Message}";
                    lastFailedCode = string.IsNullOrEmpty(currentCodeToExecute) ? lastFailedCode : currentCodeToExecute;
                    finalCodeToShowUser = lastFailedCode;

                    // Append details if available
                    if (relevantEx is HttpRequestException httpEx)
                    {
                        if (httpEx.Data.Contains("ResponseBody")) lastErrorMessage += $"\nResponse Body:\n{httpEx.Data["ResponseBody"]}";
                        if (httpEx.Data.Contains("StatusCode")) lastErrorMessage += $"\nStatus Code: {httpEx.Data["StatusCode"]}";
                    }
                    else if (relevantEx.Data.Contains("ResponseBody"))
                    {
                        lastErrorMessage += $"\nResponse Body:\n{relevantEx.Data["ResponseBody"]}";
                    }
                    else if (relevantEx is InvalidOperationException && relevantEx.InnerException is JsonException)
                    {
                        lastErrorMessage += $"\nDetails: Invalid JSON response. {relevantEx.InnerException.Message}";
                    }
                    else if (relevantEx.Data.Contains("BlockReason")) // Check for safety block info
                    {
                        lastErrorMessage += $"\nReason: {relevantEx.Data["BlockReason"]}";
                    }

                    if (attempt == MaxRetryAttempts)
                    {
                        // Final failure message will be constructed after the loop
                    }
                    continue; // Proceed to the next attempt
                }
                catch (Exception apiEx) // Handle other API/parsing exceptions directly
                {
                    Debug.WriteLine($"Gemini API Call or Processing Failed (Attempt {attempt}): {apiEx}");
                    lastErrorMessage = $"Attempt {attempt}: Error calling Gemini API or processing its response: {apiEx.Message}";
                    lastFailedCode = string.IsNullOrEmpty(currentCodeToExecute) ? lastFailedCode : currentCodeToExecute;
                    finalCodeToShowUser = lastFailedCode;

                    // Append details if available
                    if (apiEx is HttpRequestException httpEx)
                    {
                        if (httpEx.Data.Contains("ResponseBody")) lastErrorMessage += $"\nResponse Body:\n{httpEx.Data["ResponseBody"]}";
                        if (httpEx.Data.Contains("StatusCode")) lastErrorMessage += $"\nStatus Code: {httpEx.Data["StatusCode"]}";
                    }
                    else if (apiEx.Data.Contains("ResponseBody"))
                    {
                        lastErrorMessage += $"\nResponse Body:\n{apiEx.Data["ResponseBody"]}";
                    }
                    else if (apiEx is InvalidOperationException && apiEx.InnerException is JsonException)
                    {
                        lastErrorMessage += $"\nDetails: Invalid JSON response. {apiEx.InnerException.Message}";
                    }
                    else if (apiEx.Data.Contains("BlockReason")) // Check for safety block info
                    {
                        lastErrorMessage += $"\nReason: {apiEx.Data["BlockReason"]}";
                    }


                    if (attempt == MaxRetryAttempts)
                    {
                        // Final failure message will be constructed after the loop
                    }
                    continue; // Proceed to the next attempt
                }

                // --- 4. Code Review and Execution ---
                if (string.IsNullOrWhiteSpace(currentCodeToExecute))
                {
                    Debug.WriteLine($"Warning: Empty code generated/extracted on attempt {attempt}. Treating as failure.");
                    lastErrorMessage = $"Attempt {attempt}: No executable code was generated or extracted from the AI response.";
                    lastFailedCode = string.Empty;
                    finalCodeToShowUser = lastFailedCode; // Keep the previously failed code (if any) for the final report
                    if (attempt == MaxRetryAttempts)
                    {
                        // Final failure message will be constructed after the loop
                    }
                    continue; // Skip execution, try again
                }

                // 4a. Offer to Execute
                TaskDialog codeDialog = new TaskDialog("Review Code");
                codeDialog.MainInstruction = $"Review Code (Attempt {attempt}/{MaxRetryAttempts})";
                codeDialog.MainContent = "Gemini generated the following Python code based on your request.\nReview carefully before executing.";
                codeDialog.ExpandedContent = currentCodeToExecute.Length > 1000 ? currentCodeToExecute.Substring(0, 1000) + "\n\n[... Code truncated for preview ...]" : currentCodeToExecute;
                codeDialog.CommonButtons = TaskDialogCommonButtons.Cancel;
                codeDialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Execute this code (Inside Transaction)");
                codeDialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Copy code to Clipboard and Cancel");

                TaskDialogResult tdr = codeDialog.Show();

                if (tdr == TaskDialogResult.CommandLink1) // Execute
                {
                    string scriptOutput = string.Empty;
                    string scriptError = string.Empty;
                    bool currentAttemptSuccess = false;

                    using (Transaction pyTransaction = new Transaction(doc, $"Execute Gemini Python Code Attempt {attempt}"))
                    {
                        try
                        {
                            pyTransaction.Start();
                            Debug.WriteLine($"Attempting to execute code (Attempt {attempt}) inside a transaction...");
                            currentAttemptSuccess = ExecuteGeneratedPythonCode(currentCodeToExecute, doc, uidoc, uiapp, out scriptOutput, out scriptError);
                            lastScriptOutput = scriptOutput; // Store output

                            if (currentAttemptSuccess)
                            {
                                pyTransaction.Commit();
                                uidoc.RefreshActiveView();
                                Debug.WriteLine($"Attempt {attempt} SUCCEEDED. Transaction committed.");
                                Debug.WriteLine($"Script output (length: {scriptOutput?.Length ?? 0}):");
                                Debug.WriteLine(scriptOutput);
                                overallSuccess = true;
                                finalCodeToShowUser = currentCodeToExecute;
                                finalSuccessMessage = "Python code executed successfully."; // Reset success message

                                // --- Auto-copy successful code to clipboard ---
                                bool copiedToClipboard = false;
                                try
                                {
                                    string codeToCopy = currentCodeToExecute;
                                    Thread staThread = new Thread(() => Clipboard.SetText(codeToCopy));
                                    staThread.SetApartmentState(ApartmentState.STA);
                                    staThread.Start();
                                    staThread.Join();
                                    copiedToClipboard = true;
                                    Debug.WriteLine("Successfully copied executed code to clipboard.");
                                }
                                catch (Exception clipEx)
                                {
                                    Debug.WriteLine($"WARNING: Failed to copy executed code to clipboard automatically: {clipEx.Message}");
                                }
                                if (copiedToClipboard)
                                {
                                    finalSuccessMessage = "Python code executed successfully and copied to clipboard.";
                                }

                                // --- IMPROVED: Export Handling with Excel Support ---
                                Debug.WriteLine("Checking for export markers in output...");

                                // Check if output contains export marker
                                if (!string.IsNullOrWhiteSpace(scriptOutput))
                                {
                                    // Split output by line breaks (handle both \r\n and \n)
                                    string[] outputLines = scriptOutput.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

                                    // Find the export marker line
                                    int markerLineIndex = -1;
                                    for (int i = 0; i < outputLines.Length; i++)
                                    {
                                        if (outputLines[i].TrimStart().StartsWith(ExportMarkerPrefix, StringComparison.OrdinalIgnoreCase))
                                        {
                                            markerLineIndex = i;
                                            break;
                                        }
                                    }

                                    if (markerLineIndex >= 0)
                                    {
                                        Debug.WriteLine($"Export marker found at line {markerLineIndex}");
                                        string markerLine = outputLines[markerLineIndex].Trim();

                                        // Build data string from all lines after the marker
                                        StringBuilder dataBuilder = new StringBuilder();
                                        for (int i = markerLineIndex + 1; i < outputLines.Length; i++)
                                        {
                                            dataBuilder.AppendLine(outputLines[i]);
                                        }
                                        string dataToSave = dataBuilder.ToString();

                                        Debug.WriteLine($"Export marker: '{markerLine}'");
                                        Debug.WriteLine($"Data to save (length: {dataToSave.Length}), first 100 chars: {(dataToSave.Length > 100 ? dataToSave.Substring(0, 100) : dataToSave)}...");

                                        string[] parts = markerLine.Split(new string[] { "::" }, StringSplitOptions.None);

                                        if (parts.Length == 3 && parts[0].Trim().Equals("EXPORT", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(parts[1]) && !string.IsNullOrWhiteSpace(parts[2]))
                                        {
                                            Debug.WriteLine("Export marker validated. Proceeding with save dialog.");
                                            string format = parts[1].Trim().ToUpperInvariant();
                                            string suggestedFilenameRaw = parts[2].Trim();

                                            // Determine file extension and filter based on format
                                            string fileExtension;
                                            string filter;

                                            switch (format)
                                            {
                                                case "EXCEL":
                                                    fileExtension = ".xlsx";
                                                    filter = "Excel files (*.xlsx)|*.xlsx|All files (*.*)|*.*";
                                                    break;
                                                case "CSV":
                                                    fileExtension = ".csv";
                                                    filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*";
                                                    break;
                                                default: // Default to TXT
                                                    fileExtension = ".txt";
                                                    filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*";
                                                    break;
                                            }

                                            char[] invalidChars = System.IO.Path.GetInvalidFileNameChars();
                                            string sanitizedBaseName = new string(suggestedFilenameRaw.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray()).Trim();
                                            sanitizedBaseName = System.IO.Path.GetFileNameWithoutExtension(sanitizedBaseName);
                                            if (string.IsNullOrWhiteSpace(sanitizedBaseName))
                                            {
                                                sanitizedBaseName = $"exported_data_{DateTime.Now:yyyyMMddHHmmss}";
                                            }
                                            string suggestedFilename = sanitizedBaseName + fileExtension;

                                            using (SaveFileDialog saveDialog = new SaveFileDialog())
                                            {
                                                saveDialog.Title = "Save Exported Data";
                                                saveDialog.Filter = filter;
                                                saveDialog.FileName = suggestedFilename;
                                                saveDialog.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

                                                // Show the dialog
                                                NativeWindow revitWindow = new NativeWindow();
                                                try
                                                {
                                                    revitWindow.AssignHandle(uiapp.MainWindowHandle);
                                                    DialogResult result = saveDialog.ShowDialog(revitWindow);

                                                    if (result == DialogResult.OK && !string.IsNullOrWhiteSpace(saveDialog.FileName))
                                                    {
                                                        try
                                                        {
                                                            if (format == "EXCEL")
                                                            {
                                                                // Save as Excel file
                                                                SaveAsExcel(dataToSave, saveDialog.FileName);
                                                                Debug.WriteLine($"Data successfully exported to Excel: {saveDialog.FileName}");
                                                            }
                                                            else
                                                            {
                                                                // Original code for saving TXT/CSV
                                                                File.WriteAllText(saveDialog.FileName, dataToSave, Encoding.UTF8);
                                                                Debug.WriteLine($"Data successfully exported to: {saveDialog.FileName}");
                                                            }

                                                            finalSuccessMessage += $"\nData exported to:\n{saveDialog.FileName}"; // Append export info
                                                            lastScriptOutput = "[Data content saved to external file]";
                                                        }
                                                        catch (Exception fileEx)
                                                        {
                                                            Debug.WriteLine($"Error saving exported file: {fileEx}");
                                                            TaskDialog.Show("Export Error", $"Failed to save the file:\n{fileEx.Message}");
                                                            finalSuccessMessage += "\n(but failed to save the exported data)."; // Append export status
                                                        }
                                                    }
                                                    else
                                                    {
                                                        Debug.WriteLine("User cancelled the save file dialog.");
                                                        finalSuccessMessage += "\n(Data export was cancelled by the user)."; // Append export status
                                                    }
                                                }
                                                catch (Exception dialogEx)
                                                {
                                                    Debug.WriteLine($"Error showing save dialog: {dialogEx.Message}");
                                                    // Try showing the dialog without owner
                                                    try
                                                    {
                                                        DialogResult result = saveDialog.ShowDialog();

                                                        if (result == DialogResult.OK && !string.IsNullOrWhiteSpace(saveDialog.FileName))
                                                        {
                                                            if (format == "EXCEL")
                                                            {
                                                                // Save as Excel file
                                                                SaveAsExcel(dataToSave, saveDialog.FileName);
                                                                Debug.WriteLine($"Data successfully exported to Excel: {saveDialog.FileName}");
                                                            }
                                                            else
                                                            {
                                                                // Original code for saving TXT/CSV
                                                                File.WriteAllText(saveDialog.FileName, dataToSave, Encoding.UTF8);
                                                                Debug.WriteLine($"Data successfully exported to: {saveDialog.FileName}");
                                                            }

                                                            finalSuccessMessage += $"\nData exported to:\n{saveDialog.FileName}";
                                                            lastScriptOutput = "[Data content saved to external file]";
                                                        }
                                                    }
                                                    catch (Exception saveEx)
                                                    {
                                                        Debug.WriteLine($"Error in fallback save dialog: {saveEx.Message}");
                                                        finalSuccessMessage += "\n(but failed to save the exported data).";
                                                    }
                                                }
                                                finally
                                                {
                                                    try { revitWindow.ReleaseHandle(); } catch { }
                                                }
                                            }
                                        }
                                        else
                                        {
                                            finalSuccessMessage += "\n(but the export marker was malformed)."; // Append export status
                                            Debug.WriteLine($"Warning: Malformed export marker line detected: '{markerLine}'");
                                        }
                                    }
                                    else
                                    {
                                        Debug.WriteLine("No valid export marker detected in output.");
                                    }
                                }
                                else
                                {
                                    Debug.WriteLine("Output is empty, no export marker to process.");
                                }
                                // --- END: IMPROVED Export Handling ---


                                // --- Save successful script ---
                                try
                                {
                                    string assemblyLocation = Assembly.GetExecutingAssembly().Location;
                                    string pluginDirectory = System.IO.Path.GetDirectoryName(assemblyLocation);
                                    string saveFolderName = "GeneratedSuccessfulCode";
                                    string saveFolderPath = System.IO.Path.Combine(pluginDirectory, saveFolderName);
                                    Directory.CreateDirectory(saveFolderPath);
                                    string sanitizedPromptPart = "UserRequest";
                                    int maxPromptLength = 60;
                                    if (!string.IsNullOrWhiteSpace(userPrompt))
                                    {
                                        char[] invalidFileNameChars = System.IO.Path.GetInvalidFileNameChars();
                                        var cleanedChars = userPrompt.Select(ch => invalidFileNameChars.Contains(ch) ? '_' : ch);
                                        string cleanedString = new string(cleanedChars.ToArray()).Trim('_', ' ');
                                        cleanedString = System.Text.RegularExpressions.Regex.Replace(cleanedString, @"[\s_]+", "_");
                                        if (cleanedString.Length > maxPromptLength) { cleanedString = cleanedString.Substring(0, maxPromptLength).TrimEnd('_'); }
                                        if (!string.IsNullOrWhiteSpace(cleanedString)) { sanitizedPromptPart = cleanedString; }
                                    }
                                    string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                                    string fileName = $"{sanitizedPromptPart}_{timestamp}.py";
                                    string filePath = System.IO.Path.Combine(saveFolderPath, fileName);
                                    File.WriteAllText(filePath, currentCodeToExecute, Encoding.UTF8);
                                    Debug.WriteLine($"Successfully saved generated code to: {filePath}");
                                }
                                catch (Exception saveEx) { Debug.WriteLine($"WARNING: Failed to save the successful script. Error: {saveEx.Message}"); }
                                // --- END: Save successful script ---

                                break; // Exit loop on success
                            }
                            else // Execution failed
                            {
                                pyTransaction.RollBack();
                                Debug.WriteLine($"Attempt {attempt} FAILED. Transaction rolled back. Error: {scriptError}");
                                lastErrorMessage = scriptError; // Store Python execution error
                                lastFailedCode = currentCodeToExecute;
                                finalCodeToShowUser = currentCodeToExecute;
                                // Continue loop for retry
                            }
                        }
                        catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                        {
                            Debug.WriteLine($"Transaction cancelled by user during attempt {attempt}.");
                            if (pyTransaction.GetStatus() == TransactionStatus.Started) { try { pyTransaction.RollBack(); } catch { } }
                            message = "Python code execution cancelled by user during Revit operation.";
                            lastErrorMessage = message; // Store cancellation reason
                            overallSuccess = false;
                            finalCodeToShowUser = currentCodeToExecute;
                            goto EndLoop; // Exit outer loop immediately
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Critical error during Python execution transaction management (Attempt {attempt}): {ex}");
                            if (pyTransaction.GetStatus() == TransactionStatus.Started) { try { pyTransaction.RollBack(); } catch { } }
                            lastErrorMessage = $"Attempt {attempt}: Critical error executing Python code wrapper: {ex.Message}";
                            lastFailedCode = currentCodeToExecute;
                            finalCodeToShowUser = currentCodeToExecute;
                            overallSuccess = false;
                            message = lastErrorMessage; // Set final message
                            goto EndLoop; // Exit outer loop immediately
                        }
                    } // End Transaction Using Block
                }
                else if (tdr == TaskDialogResult.CommandLink2) // Copy and Cancel
                {
                    try
                    {
                        string codeToCopy = currentCodeToExecute;
                        Thread staThread = new Thread(() => Clipboard.SetText(codeToCopy));
                        staThread.SetApartmentState(ApartmentState.STA);
                        staThread.Start();
                        staThread.Join();
                        TaskDialog.Show("Clipboard", "Code copied to clipboard. Operation cancelled.");
                    }
                    catch (Exception clipEx)
                    {
                        TaskDialog.Show("Clipboard Error", $"Could not copy code to clipboard: {clipEx.Message}");
                        Debug.WriteLine($"Clipboard Error: {clipEx}");
                    }
                    message = "Operation cancelled by user after copying code.";
                    overallSuccess = false;
                    finalCodeToShowUser = currentCodeToExecute;
                    goto EndLoop; // Exit outer loop immediately
                }
                else // Cancelled review (TaskDialogResult.Cancel or closed)
                {
                    message = "Operation cancelled by user during code review.";
                    overallSuccess = false;
                    finalCodeToShowUser = currentCodeToExecute;
                    goto EndLoop; // Exit outer loop immediately
                }
            } // End For Loop

        EndLoop:; // Label for goto statements

            // --- Step 5: Final Reporting ---
            if (overallSuccess)
            {
                TaskDialog successDialog = new TaskDialog("Execution Succeeded");
                successDialog.MainInstruction = finalSuccessMessage;
                successDialog.MainContent = "Final Output from Script (if any):";
                successDialog.ExpandedContent = string.IsNullOrWhiteSpace(lastScriptOutput) ? "[No output]" : lastScriptOutput;
                successDialog.CommonButtons = TaskDialogCommonButtons.Close;
                successDialog.Show();
                return Result.Succeeded;
            }
            else // Failure or Cancellation path
            {
                // Construct failure message if not already set by cancellation or critical error
                if (string.IsNullOrWhiteSpace(message)) // If no specific cancel/error message was set...
                {
                    // Construct a general failure message using MaxRetryAttempts
                    message = $"Code execution did not succeed within the {MaxRetryAttempts} allowed attempts.";
                    if (!string.IsNullOrWhiteSpace(lastErrorMessage))
                    {
                        // Append the last recorded error if available
                        message += $"\n\nLast Error:\n{lastErrorMessage}";
                    }
                    else
                    {
                        // Indicate if no specific error was captured (e.g., failed API calls without details)
                        message += "\nNo specific error message was captured, but the process failed.";
                    }
                }
                // Ensure lastErrorMessage is included if message *was* set (e.g., by cancellation) but doesn't already contain the technical error.
                else if (!string.IsNullOrWhiteSpace(lastErrorMessage) && !message.Contains(lastErrorMessage))
                {
                    message += $"\n\nLast Recorded Error:\n{lastErrorMessage}";
                }


                // Append last output only if relevant and not already part of the message
                if (!string.IsNullOrWhiteSpace(lastScriptOutput)
                    && !message.Contains("Output Before Error") // Avoid duplication if already added by ExecuteGeneratedPythonCode
                    && !message.Contains(lastScriptOutput)     // Avoid duplicating if it's somehow already in 'message'
                    && !lastScriptOutput.StartsWith("[Data content saved"))
                {
                    message += $"\n\nOutput before last error (if any):\n{lastScriptOutput}";
                }

                TaskDialog failureDialog = new TaskDialog("Execution Failed or Cancelled");
                failureDialog.MainInstruction = "Code Execution Failed or Cancelled";
                failureDialog.MainContent = message; // Use the constructed or pre-set message
                failureDialog.ExpandedContent = $"Last code attempted, generated, or copied:\n\n{(string.IsNullOrWhiteSpace(finalCodeToShowUser) ? "[No code available]" : finalCodeToShowUser)}";
                failureDialog.CommonButtons = TaskDialogCommonButtons.Close;

                failureDialog.Show();
                Debug.WriteLine($"Execution failed or cancelled. Final message: {message}");
                // Check message content for cancellation reason
                return message.ToLowerInvariant().Contains("cancel") ? Result.Cancelled : Result.Failed;
            }
        } // End Execute Method


        // --- Helper Methods --- 

        // --- ConstructFixItPrompt Method ---
        public string ConstructFixItPrompt(string originalUserRequest, string failedCode, string errorMessage)
        {
            if (string.IsNullOrWhiteSpace(failedCode)) failedCode = "# No previous code was available or execution failed before code generation.";
            if (string.IsNullOrWhiteSpace(errorMessage)) errorMessage = "# No specific error message was captured, but the previous attempt failed.";

            // Escape curly braces for string interpolation/formatting
            string escapedErrorMessage = errorMessage.Replace("{", "{{").Replace("}", "}}");
            string escapedFailedCode = failedCode.Replace("{", "{{").Replace("}", "}}");
            string escapedOriginalRequest = originalUserRequest.Replace("{", "{{").Replace("}", "}}");

            // Using verbatim interpolated string literal for readability
            string fixPromptTemplate = $@"ROLE: You are an expert Revit API assistant generating Python code using IronPython for Autodesk Revit.

TASK: Your previous attempt to generate Python code resulted in an error when executed within the Revit environment using IronPython. Analyze the error message and the failed code provided below. Your goal is to provide a corrected Python script ONLY that addresses the error and fulfills the original user request.

ORIGINAL USER REQUEST:
---
{escapedOriginalRequest}
---

FAILED PYTHON CODE (Executed via IronPython):
```python
{escapedFailedCode}
ERROR MESSAGE RECEIVED FROM IRONPYTHON/REVIT:
{escapedErrorMessage}
RESPONSE FORMAT & CONSTRAINTS (VERY IMPORTANT - FOLLOW EXACTLY):
Output ONLY the corrected Python code.
Start the response DIRECTLY with the Python code (e.g., import clr or the first functional line).
Do NOT include the markdown fence (python or) around your code block.
Do NOT include ANY introductory text, explanations, apologies, or concluding remarks. Just the code.
Do NOT manage Revit Transactions (NO Transaction(), t.Start(), t.Commit()). The C# wrapper handles this.
Assume the following variables are pre-defined and available in the script's scope:
doc: The current Revit Document (Autodesk.Revit.DB.Document)
uidoc: The current Revit UIDocument (Autodesk.Revit.UI.UIDocument)
app: The Revit Application object (Autodesk.Revit.ApplicationServices.Application)
uiapp: The Revit UIApplication object (Autodesk.Revit.UI.UIApplication)
__revit__: Often used as a reference to uiapp in some contexts.
Ensure all necessary Revit API namespaces are imported via clr.AddReference() and import ... or from ... import ... statements (e.g., clr.AddReference('RevitAPI'), from Autodesk.Revit.DB import *). Missing imports are critical failures. Include standard libraries like System if needed (clr.AddReference('System'), import System).
DATA EXPORT HANDLING: If the user request requires exporting data (e.g., to CSV, TXT, or EXCEL), use Python's print() function in a specific format ONCE at the very end of the script's output:
First Line: Print the export marker: print(""EXPORT::[FORMAT]::[FILENAME]"") where [FORMAT] is either CSV, TXT, or EXCEL and [FILENAME] is a suggested filename (e.g., element_data.csv).
Following Lines: Print the actual data string (potentially multi-line) that should be saved to the file.
Example for CSV:
# ... (code to generate csv_data_string) ...
print(""EXPORT::CSV::element_report.csv"")
print(csv_data_string)
Example for Excel (the data is still in CSV format, but will be converted to Excel by the plugin):
# ... (code to generate csv_data_string) ...
print(""EXPORT::EXCEL::element_report.xlsx"")
print(csv_data_string)
The C# code will detect this marker and handle the file saving dialog. Do not try to write files directly in Python. Only use print.
If the error cannot be fixed based on the provided information, or if the original request is impossible within the Revit API/IronPython constraints, output ONLY a single Python comment line explaining why (e.g., # Error: Cannot fix the previous error because [specific reason]. or # Error: The original request cannot be fulfilled because [specific reason].).
CORRECTED PYTHON SCRIPT (or single comment line if unfixable):
";
            return fixPromptTemplate;
        }

        // --- GenerateLlmPromptViaPython Method ---
        public string GenerateLlmPromptViaPython(string userQuery)
        {
            string assemblyLocation = Assembly.GetExecutingAssembly().Location;
            string pluginDirectory = System.IO.Path.GetDirectoryName(assemblyLocation);
            string pythonWorkingDir = System.IO.Path.Combine(pluginDirectory, "Python");
            string scriptPath = System.IO.Path.Combine(pythonWorkingDir, "generate_rag_prompt.py");
            string pythonExePath = GetPythonPathFromConfigOrEnvironment();

            if (!Directory.Exists(pythonWorkingDir)) throw new DirectoryNotFoundException($"Python working directory not found: {pythonWorkingDir}");
            if (!File.Exists(scriptPath)) throw new FileNotFoundException($"Python RAG script not found: {scriptPath}");
            if (!string.IsNullOrEmpty(pythonExePath) && !File.Exists(pythonExePath) && pythonExePath != "python.exe")
            {
                throw new FileNotFoundException($"Specified Python executable not found. Please check the path: {pythonExePath}");
            }
            else if (string.IsNullOrEmpty(pythonExePath))
            {
                throw new InvalidOperationException("Python executable path is not configured or could not be determined.");
            }

            Debug.WriteLine($"DEBUG: Using Python executable: [{pythonExePath}]");
            Debug.WriteLine($"DEBUG: Python working directory: [{pythonWorkingDir}]");
            Debug.WriteLine($"DEBUG: Python script path: [{scriptPath}]");
            Debug.WriteLine($"DEBUG: User query argument: [{userQuery}]");

            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = pythonExePath;
            startInfo.Arguments = $"{EscapeArgument(scriptPath)} {EscapeArgument(userQuery)}";
            startInfo.UseShellExecute = false;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.StandardOutputEncoding = Encoding.UTF8;
            startInfo.StandardErrorEncoding = Encoding.UTF8;
            startInfo.CreateNoWindow = true;
            startInfo.WorkingDirectory = pythonWorkingDir;

            StringBuilder outputBuilder = new StringBuilder();
            StringBuilder errorBuilder = new StringBuilder();
            int timeoutMilliseconds = 120000;

            using (Process process = new Process { StartInfo = startInfo })
            {
                process.OutputDataReceived += (sender, args) => { if (args.Data != null) outputBuilder.AppendLine(args.Data); };
                process.ErrorDataReceived += (sender, args) =>
                {
                    if (args.Data != null)
                    {
                        errorBuilder.AppendLine(args.Data);
                        Debug.WriteLine($"PY_STDERR: {args.Data}");
                    }
                };

                try
                {
                    process.Start();
                    Debug.WriteLine($"DEBUG: Started Python process (ID: {process.Id}). Waiting for exit (Timeout: {timeoutMilliseconds / 1000}s)...");
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    bool exited = process.WaitForExit(timeoutMilliseconds);
                    string errors = errorBuilder.ToString().Trim();
                    string output = outputBuilder.ToString().Trim();

                    if (!exited)
                    {
                        try { if (!process.HasExited) process.Kill(); } catch (Exception kex) { Debug.WriteLine($"ERROR: Failed to kill Python process after timeout: {kex.Message}"); }
                        throw new TimeoutException($"Python RAG script execution timed out after {timeoutMilliseconds / 1000} seconds. Captured STDERR (if any):\n{errors}");
                    }

                    Debug.WriteLine($"DEBUG: Python process (ID: {process.Id}) exited with code {process.ExitCode}.");

                    if (process.ExitCode != 0)
                    {
                        throw new Exception($"Python script failed with Exit Code: {process.ExitCode}.\n--- STDERR ---\n{errors}\n--- STDOUT ---\n{output}");
                    }
                    if (!string.IsNullOrEmpty(errors))
                    {
                        Debug.WriteLine($"--- Python Script Finished (Exit Code 0) but produced STDERR Output ---");
                    }
                    if (string.IsNullOrWhiteSpace(output))
                    {
                        Debug.WriteLine("WARNING: Python script exited successfully (Code 0) but produced no output to STDOUT.");
                    }
                    return output;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"ERROR: Exception during Python process execution: {ex}");
                    throw;
                }
            }
        }

        // --- Helper to get Python Path ---
        private string GetPythonPathFromConfigOrEnvironment()
        {
            string pluginSpecificEnvVar = "REVIT_GEMINI_PYTHON_PATH";
            string pathFromEnv = Environment.GetEnvironmentVariable(pluginSpecificEnvVar);

            if (!string.IsNullOrWhiteSpace(pathFromEnv))
            {
                Debug.WriteLine($"Using Python path from environment variable '{pluginSpecificEnvVar}': {pathFromEnv}");
                return pathFromEnv;
            }
            // Add config file logic here if needed
            Debug.WriteLine("Python path not found in specific environment variable or config. Defaulting to 'python.exe' (requires Python in system PATH).");
            return "python.exe";
        }

        // --- CallGeminiApiAsync Method ---
        public async Task<string> CallGeminiApiAsync(string ragPrompt)
        {
            string apiKey = Environment.GetEnvironmentVariable(GoogleApiKeyEnvVariable);
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new InvalidOperationException($"Google API key not found. Please set the '{GoogleApiKeyEnvVariable}' environment variable.");
            }
            string endpoint = $"https://generativelanguage.googleapis.com/v1beta/models/{GeminiModelId}:generateContent?key={apiKey}";
            Debug.WriteLine($"DEBUG: Gemini API Endpoint: {endpoint}");
            using (HttpClient client = new HttpClient())
            {
                client.Timeout = TimeSpan.FromSeconds(180);
                var requestBody = new
                {
                    contents = new[] { new { role = "user", parts = new[] { new { text = ragPrompt } } } },
                    generationConfig = new { maxOutputTokens = MaxOutputTokens },
                    safetySettings = new[] {
                        new { category = "HARM_CATEGORY_HARASSMENT", threshold = "BLOCK_MEDIUM_AND_ABOVE" },
                        new { category = "HARM_CATEGORY_HATE_SPEECH", threshold = "BLOCK_MEDIUM_AND_ABOVE" },
                        new { category = "HARM_CATEGORY_SEXUALLY_EXPLICIT", threshold = "BLOCK_MEDIUM_AND_ABOVE" },
                        new { category = "HARM_CATEGORY_DANGEROUS_CONTENT", threshold = "BLOCK_MEDIUM_AND_ABOVE" }
                    }
                };
                string jsonRequestBody = JsonConvert.SerializeObject(requestBody, Formatting.None);
                using (StringContent content = new StringContent(jsonRequestBody, Encoding.UTF8, "application/json"))
                {
                    try
                    {
                        HttpResponseMessage response = await client.PostAsync(endpoint, content).ConfigureAwait(false);
                        string responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        Debug.WriteLine($"--- Received Response from Gemini (Status: {response.StatusCode}) ---");
                        if (!response.IsSuccessStatusCode)
                        {
                            string errorDetails = responseBody;
                            try { JObject errorJson = JObject.Parse(responseBody); errorDetails = errorJson?["error"]?["message"]?.ToString() ?? errorDetails; } catch (JsonException) { }
                            string exceptionMessage = $"Gemini API request failed.\nStatus Code: {response.StatusCode} ({(int)response.StatusCode})\nDetails: {errorDetails}";
                            var ex = new HttpRequestException(exceptionMessage);
                            ex.Data.Add("ResponseBody", responseBody);
                            ex.Data.Add("StatusCode", response.StatusCode);
                            throw ex;
                        }
                        return responseBody;
                    }
                    catch (HttpRequestException httpEx) { Debug.WriteLine($"ERROR: HTTP request exception during Gemini API call: {httpEx.Message}"); throw; }
                    catch (TaskCanceledException tcEx) { Debug.WriteLine($"ERROR: Gemini API call timed out: {tcEx.Message}"); throw new TimeoutException("The request to the Gemini API timed out.", tcEx); }
                    catch (Exception ex) { Debug.WriteLine($"ERROR: Unexpected exception during Gemini API call: {ex}"); throw new Exception("An unexpected error occurred while communicating with the Gemini API.", ex); }
                }
            }
        }

        // --- ParseGeminiResponse Method ---
        public string ParseGeminiResponse(string responseBody)
        {
            if (string.IsNullOrWhiteSpace(responseBody)) { Debug.WriteLine("ERROR: ParseGeminiResponse received null or empty input."); return null; }
            try
            {
                JObject jsonResponse = JObject.Parse(responseBody);
                var promptFeedback = jsonResponse["promptFeedback"];
                if (promptFeedback != null)
                {
                    string blockReason = promptFeedback?["blockReason"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(blockReason))
                    {
                        string blockDetails = promptFeedback?["blockReasonMessage"]?.ToString();
                        string safetyRatings = promptFeedback?["safetyRatings"]?.ToString(Formatting.None);
                        string errorMessage = $"Gemini blocked the prompt. Reason: {blockReason}";
                        if (!string.IsNullOrWhiteSpace(blockDetails)) errorMessage += $" - Details: {blockDetails}";
                        Debug.WriteLine($"ERROR: {errorMessage}. Safety Ratings (Prompt): {safetyRatings ?? "N/A"}");
                        var ex = new Exception(errorMessage);
                        ex.Data.Add("BlockReason", blockReason);
                        ex.Data.Add("ResponseBody", responseBody);
                        throw ex;
                    }
                }
                JToken candidatesArray = jsonResponse["candidates"];
                if (candidatesArray == null || !candidatesArray.HasValues || !(candidatesArray is JArray) || !candidatesArray.Any())
                {
                    if (promptFeedback?["blockReason"] != null) { throw new Exception($"Gemini response blocked (Reason: {promptFeedback["blockReason"]}), no candidates provided."); }
                    else { Debug.WriteLine("ERROR: Gemini response parsed, but 'candidates' array is missing, empty, or not an array."); Debug.WriteLine($"--- Response Body ---\n{responseBody}\n--- End Response Body ---"); throw new InvalidOperationException("Gemini response did not contain expected candidate data."); }
                }
                JToken candidate = candidatesArray[0];
                if (candidate == null) { Debug.WriteLine("ERROR: Gemini response 'candidates' array exists but the first element is null."); return null; }
                string finishReason = candidate["finishReason"]?.ToString();
                Debug.WriteLine($"Gemini Finish Reason: {finishReason}");
                string safetyRatingsOutput = candidate["safetyRatings"]?.ToString(Formatting.None);
                if (!string.IsNullOrWhiteSpace(safetyRatingsOutput)) { Debug.WriteLine($"Safety Ratings (Output): {safetyRatingsOutput}"); }
                switch (finishReason)
                {
                    case "STOP": break;
                    case "SAFETY": Debug.WriteLine($"WARNING: Gemini response potentially altered or blocked due to SAFETY filters on the generated output."); break;
                    case "MAX_TOKENS": Debug.WriteLine("WARNING: Gemini response may have been cut short because the maximum output token limit was reached."); break;
                    case "RECITATION": Debug.WriteLine("ERROR: Gemini response blocked due to recitation policy."); throw new Exception("Gemini response generation stopped due to recitation policy.");
                    case null: Debug.WriteLine("WARNING: Gemini response candidate missing 'finishReason'. Proceeding, but status is uncertain."); break;
                    default: Debug.WriteLine($"WARNING: Gemini response generation finished with unexpected reason: {finishReason}. Proceeding."); break;
                }
                string generatedText = candidate?["content"]?["parts"]?[0]?["text"]?.ToString();
                if (string.IsNullOrWhiteSpace(generatedText))
                {
                    if (finishReason == "SAFETY") { Debug.WriteLine("WARNING: Gemini response finished due to SAFETY and text content is missing or empty. Returning null/empty."); return string.Empty; }
                    else { Debug.WriteLine("WARNING: Gemini response parsed, but did not contain expected text content ('candidates[0].content.parts[0].text')."); Debug.WriteLine($"--- Candidate Structure ---\n{candidate.ToString(Formatting.Indented)}\n--- End Candidate Structure ---"); return string.Empty; }
                }
                Debug.WriteLine("Successfully extracted text content from Gemini response.");
                return generatedText;
            }
            catch (JsonException jsonEx) { Debug.WriteLine($"ERROR: Failed to parse Gemini JSON response: {jsonEx.Message}"); Debug.WriteLine($"--- Response Body Start ---\n{responseBody}\n--- Response Body End ---"); var ex = new InvalidOperationException("Failed to parse Gemini JSON response.", jsonEx); ex.Data.Add("ResponseBody", responseBody); throw ex; }
            catch (Exception ex) when (!(ex is InvalidOperationException) && !(ex is HttpRequestException) && !(ex is TimeoutException)) { Debug.WriteLine($"ERROR: Unexpected error processing Gemini response content: {ex.Message}"); if (!ex.Data.Contains("ResponseBody")) { ex.Data.Add("ResponseBody", responseBody); } throw; }
        }

        // --- COMPLETELY REDESIGNED: ExecuteGeneratedPythonCode Method ---
        public bool ExecuteGeneratedPythonCode(string pythonCode, Document doc, UIDocument uidoc, UIApplication uiapp, out string output, out string errorMessage)
        {
            output = string.Empty;
            errorMessage = string.Empty;
            ScriptEngine engine = null;
            ScriptScope scope = null;
            StringBuilder outputCapture = new StringBuilder();

            try
            {
                Debug.WriteLine("--- Setting up IronPython Engine with Custom Output Capture ---");

                // Create a custom StringWriter to capture output
                StringWriter customOutputWriter = new StringWriter(outputCapture);

                // Create engine with completely different approach to output capture
                var engineOptions = new Dictionary<string, object>();
                engine = Python.CreateEngine(engineOptions);

                // Use the custom StringWriter for both stdout and stderr
                engine.Runtime.IO.SetOutput(Stream.Null, customOutputWriter);
                engine.Runtime.IO.SetErrorOutput(Stream.Null, customOutputWriter);

                // Create scope and load necessary assemblies
                scope = engine.CreateScope();
                Debug.WriteLine("Loading Revit API and standard assemblies into IronPython...");
                try
                {
                    Assembly apiAssembly = typeof(Document).Assembly;
                    Assembly uiAssembly = typeof(TaskDialog).Assembly;
                    if (apiAssembly != null) engine.Runtime.LoadAssembly(apiAssembly); else Debug.WriteLine("Warning: Could not load RevitAPI assembly.");
                    if (uiAssembly != null) engine.Runtime.LoadAssembly(uiAssembly); else Debug.WriteLine("Warning: Could not load RevitAPIUI assembly.");
                    engine.Runtime.LoadAssembly(typeof(List<>).Assembly);
                    engine.Runtime.LoadAssembly(typeof(System.Linq.Enumerable).Assembly);
                    engine.Runtime.LoadAssembly(typeof(System.IO.Path).Assembly);
                    engine.Runtime.LoadAssembly(typeof(System.Windows.Forms.Form).Assembly);
                    engine.Runtime.LoadAssembly(typeof(System.Net.Http.HttpClient).Assembly);
                }
                catch (Exception asmEx)
                {
                    errorMessage = $"Fatal Error: Failed to load required assemblies into IronPython: {asmEx.Message}";
                    Debug.WriteLine(errorMessage);
                    return false;
                }

                // Set up necessary variables
                scope.SetVariable("doc", doc);
                scope.SetVariable("uidoc", uidoc);
                scope.SetVariable("app", uiapp.Application);
                scope.SetVariable("uiapp", uiapp);
                scope.SetVariable("__revit__", uiapp);

                // Add a custom output capture function that writes directly to our StringBuilder
                string preambleScript = @"
import sys
real_stdout = sys.stdout
real_stderr = sys.stderr

class CustomWriter:
    def __init__(self):
        self.buffer = []
        
    def write(self, text):
        real_stdout.write(text)
        self.buffer.append(text)
        
    def flush(self):
        real_stdout.flush()
        
sys.stdout = CustomWriter()
sys.stderr = CustomWriter()

# Define a helper that will guarantee output works
def capture_output(func):
    def wrapper(*args, **kwargs):
        output = func(*args, **kwargs)
        sys.stdout.flush()
        return output
    return wrapper

# Override print to ensure it works correctly
original_print = print
def safe_print(*args, **kwargs):
    result = original_print(*args, **kwargs)
    sys.stdout.flush()
    return result
print = safe_print
";

                // Execute the preamble script to set up our custom output handling
                ScriptSource preambleSource = engine.CreateScriptSourceFromString(preambleScript, SourceCodeKind.Statements);
                preambleSource.Execute(scope);

                // Execute the actual Python code
                Debug.WriteLine("--- Executing Generated Python Code with Custom Output Capture ---");
                ScriptSource source = engine.CreateScriptSourceFromString(pythonCode, SourceCodeKind.Statements);
                source.Execute(scope);
                Debug.WriteLine("--- Python Execution Completed Successfully ---");

                // Clean up and finalize output capture
                string cleanupScript = @"
# Get all captured output from our custom writer
output_buffer = ''.join(sys.stdout.buffer)
";
                ScriptSource cleanupSource = engine.CreateScriptSourceFromString(cleanupScript, SourceCodeKind.Statements);
                cleanupSource.Execute(scope);

                // Retrieve the captured output from our custom scope
                object bufferObj = scope.GetVariable("output_buffer");
                string capturedOutput = bufferObj?.ToString() ?? string.Empty;

                // Combine both our StringBuilder capture and the direct Python capture
                output = outputCapture.ToString();
                if (!string.IsNullOrEmpty(capturedOutput))
                {
                    if (string.IsNullOrEmpty(output))
                    {
                        output = capturedOutput;
                    }
                    else
                    {
                        output += capturedOutput;
                    }
                }

                // As a fallback, try to get output directly from our custom writer buffer
                if (string.IsNullOrWhiteSpace(output))
                {
                    try
                    {
                        var stdoutWriter = scope.GetVariable("sys").GetMember("stdout");
                        var buffer = stdoutWriter.GetMember("buffer");
                        if (buffer != null)
                        {
                            var outputList = buffer as IEnumerable<object>;
                            if (outputList != null)
                            {
                                output = string.Join("", outputList.Select(o => o.ToString()));
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Fallback output capture failed: {ex.Message}");
                    }
                }

                Debug.WriteLine($"--- Python Output Captured (Length: {output?.Length ?? 0}) ---");
                Debug.WriteLine($"--- First 500 characters of output: ---");
                Debug.WriteLine(output?.Length > 500 ? output.Substring(0, 500) + "..." : output);

                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"!!! IronPython Execution Error: {ex.GetType().Name} !!!");
                var exceptionOperations = engine?.GetService<ExceptionOperations>();
                string formattedException = (exceptionOperations != null)
                    ? exceptionOperations.FormatException(ex)
                    : ex.ToString();

                errorMessage = $"Python Execution Error:\n{formattedException}";
                Debug.WriteLine($"Formatted Error:\n{errorMessage}");

                // Try to get any partial output that was captured before the error
                output = outputCapture.ToString();
                if (!string.IsNullOrWhiteSpace(output))
                {
                    Debug.WriteLine($"--- Partial Output Before Error ---\n{output}\n---------------------");
                    errorMessage += $"\n\n--- Output Before Error ---\n{output}";
                }

                return false;
            }
        }

        // --- ExtractPythonCode Method ---
        public static string ExtractPythonCode(string rawContent)
        {
            if (string.IsNullOrWhiteSpace(rawContent)) return string.Empty;
            string code = rawContent.Trim(); string lowerCode = code.ToLowerInvariant();
            const string pythonFenceStart = "```python"; const string genericFenceStart = "```"; const string fenceEnd = "```";
            int startCodePos = -1; int endCodePos = -1;
            int pythonFenceStartPos = lowerCode.IndexOf(pythonFenceStart);
            if (pythonFenceStartPos != -1)
            {
                startCodePos = pythonFenceStartPos + pythonFenceStart.Length;
                if (startCodePos < code.Length) { if (code[startCodePos] == '\r' && startCodePos + 1 < code.Length && code[startCodePos + 1] == '\n') startCodePos += 2; else if (code[startCodePos] == '\n') startCodePos++; }
                endCodePos = code.IndexOf(fenceEnd, startCodePos);
            }
            if (startCodePos == -1)
            {
                int genericFenceStartPos = lowerCode.IndexOf(genericFenceStart);
                if (genericFenceStartPos != -1)
                {
                    startCodePos = genericFenceStartPos + genericFenceStart.Length;
                    if (startCodePos < code.Length) { if (code[startCodePos] == '\r' && startCodePos + 1 < code.Length && code[startCodePos + 1] == '\n') startCodePos += 2; else if (code[startCodePos] == '\n') startCodePos++; }
                    endCodePos = code.IndexOf(fenceEnd, startCodePos);
                }
            }
            if (startCodePos != -1)
            {
                if (endCodePos != -1) { Debug.WriteLine("Found fenced code block. Extracting content within fences."); return code.Substring(startCodePos, endCodePos - startCodePos).Trim(); }
                else { Debug.WriteLine("Warning: Found opening code fence but no closing fence. Extracting from opening fence to end."); return code.Substring(startCodePos).Trim(); }
            }
            Debug.WriteLine("No code fences ('```') found. Assuming entire trimmed content is the intended Python code."); return code;
        }

        // --- EscapeArgument Method ---
        public static string EscapeArgument(string arg)
        {
            if (string.IsNullOrEmpty(arg)) return "\"\""; string escaped = arg.Replace("\\", "\\\\").Replace("\"", "\\\""); return "\"" + escaped + "\"";
        }

        // --- NEW: SaveAsExcel Method for Excel export ---
        private void SaveAsExcel(string csvData, string excelFilePath)
        {
            try
            {
                // Parse the CSV data into a DataTable
                DataTable dataTable = new DataTable();

                // Split the CSV data into lines
                string[] lines = csvData.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
                if (lines.Length == 0)
                {
                    Debug.WriteLine("Warning: No data rows found in CSV for Excel conversion");
                    File.WriteAllText(excelFilePath, csvData, Encoding.UTF8);
                    return;
                }

                // Process header line
                string[] headers = ParseCsvLine(lines[0]);
                foreach (string header in headers)
                {
                    dataTable.Columns.Add(header.Trim('"')); // Remove quotes if present
                }

                // Process data lines
                for (int i = 1; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    if (string.IsNullOrEmpty(line)) continue;

                    // Use proper CSV parsing
                    string[] fields = ParseCsvLine(line);
                    DataRow row = dataTable.NewRow();

                    for (int j = 0; j < headers.Length && j < fields.Length; j++)
                    {
                        row[j] = fields[j].Trim('"'); // Remove quotes if present
                    }

                    dataTable.Rows.Add(row);
                }

                // OPTION: Basic Excel file creation using System.Data
                // This is a very basic approach without a third-party library
                // It creates an HTML table that Excel can open
                StringBuilder htmlBuilder = new StringBuilder();
                htmlBuilder.AppendLine("<html xmlns:o=\"urn:schemas-microsoft-com:office:office\" xmlns:x=\"urn:schemas-microsoft-com:office:excel\">");
                htmlBuilder.AppendLine("<head>");
                htmlBuilder.AppendLine("<meta http-equiv=\"Content-Type\" content=\"text/html; charset=UTF-8\">");
                htmlBuilder.AppendLine("<!--[if gte mso 9]><xml>");
                htmlBuilder.AppendLine("<x:ExcelWorkbook><x:ExcelWorksheets><x:ExcelWorksheet>");
                htmlBuilder.AppendLine("<x:Name>Sheet1</x:Name>");
                htmlBuilder.AppendLine("<x:WorksheetOptions><x:DisplayGridlines/></x:WorksheetOptions>");
                htmlBuilder.AppendLine("</x:ExcelWorksheet></x:ExcelWorksheets></x:ExcelWorkbook>");
                htmlBuilder.AppendLine("</xml><![endif]-->");
                htmlBuilder.AppendLine("</head>");
                htmlBuilder.AppendLine("<body>");
                htmlBuilder.AppendLine("<table border=\"1\">");

                // Add header row
                htmlBuilder.AppendLine("<tr>");
                foreach (DataColumn column in dataTable.Columns)
                {
                    htmlBuilder.AppendLine($"<th>{HtmlEncode(column.ColumnName)}</th>");
                }
                htmlBuilder.AppendLine("</tr>");

                // Add data rows
                foreach (DataRow row in dataTable.Rows)
                {
                    htmlBuilder.AppendLine("<tr>");
                    foreach (var item in row.ItemArray)
                    {
                        string value = item?.ToString() ?? string.Empty;
                        htmlBuilder.AppendLine($"<td>{HtmlEncode(value)}</td>");
                    }
                    htmlBuilder.AppendLine("</tr>");
                }

                htmlBuilder.AppendLine("</table>");
                htmlBuilder.AppendLine("</body>");
                htmlBuilder.AppendLine("</html>");

                File.WriteAllText(excelFilePath, htmlBuilder.ToString(), Encoding.UTF8);
                Debug.WriteLine($"Excel data saved using HTML table format to: {excelFilePath}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error in Excel conversion: {ex.Message}. Falling back to CSV format.");
                // Fallback to CSV
                File.WriteAllText(excelFilePath, csvData, Encoding.UTF8);
            }
        }

        // Helper method to parse CSV lines properly, handling quotes
        private string[] ParseCsvLine(string line)
        {
            List<string> result = new List<string>();
            bool inQuotes = false;
            StringBuilder field = new StringBuilder();

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];

                if (c == '"')
                {
                    // Check if this is an escaped quote (two quotes together)
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        field.Append('"');
                        i++; // Skip the next quote
                    }
                    else
                    {
                        // Toggle quote state
                        inQuotes = !inQuotes;
                    }
                }
                else if (c == ',' && !inQuotes)
                {
                    // End of field
                    result.Add(field.ToString());
                    field.Clear();
                }
                else
                {
                    field.Append(c);
                }
            }

            // Add the last field
            result.Add(field.ToString());

            return result.ToArray();
        }

        // Helper method to encode HTML for Excel export (replaces System.Web.HttpUtility)
        private string HtmlEncode(string text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;

            return text
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;")
                .Replace("'", "&#39;");
        }

    } // End Command Class

    // --- Placeholder Form ---
    // IMPORTANT: Maintain the nested namespace to match the original code structure
    namespace RevitGeminiRAG
    {
        public partial class Gemini_RAG : System.Windows.Forms.Form
        {
            public string UserPrompt { get; private set; }
            private System.Windows.Forms.TextBox promptTextBox;
            private System.Windows.Forms.Button okButton;
            private System.Windows.Forms.Button cancelButton;

            public Gemini_RAG()
            {
                InitializeComponent();
            }

            private void InitializeComponent()
            {
                this.promptTextBox = new System.Windows.Forms.TextBox();
                this.okButton = new System.Windows.Forms.Button();
                this.cancelButton = new System.Windows.Forms.Button();
                this.SuspendLayout();
                // promptTextBox
                this.promptTextBox.Anchor = ((System.Windows.Forms.AnchorStyles)((((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Bottom)
                | System.Windows.Forms.AnchorStyles.Left)
                | System.Windows.Forms.AnchorStyles.Right)));
                this.promptTextBox.Location = new System.Drawing.Point(12, 12);
                this.promptTextBox.Multiline = true;
                this.promptTextBox.Name = "promptTextBox";
                this.promptTextBox.ScrollBars = System.Windows.Forms.ScrollBars.Vertical;
                this.promptTextBox.Size = new System.Drawing.Size(460, 137);
                this.promptTextBox.TabIndex = 0;
                // okButton
                this.okButton.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Right)));
                this.okButton.Location = new System.Drawing.Point(316, 155);
                this.okButton.Name = "okButton";
                this.okButton.Size = new System.Drawing.Size(75, 23);
                this.okButton.TabIndex = 1;
                this.okButton.Text = "OK";
                this.okButton.UseVisualStyleBackColor = true;
                this.okButton.Click += new System.EventHandler(this.OkButton_Click);
                // cancelButton
                this.cancelButton.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Right)));
                this.cancelButton.DialogResult = System.Windows.Forms.DialogResult.Cancel;
                this.cancelButton.Location = new System.Drawing.Point(397, 155);
                this.cancelButton.Name = "cancelButton";
                this.cancelButton.Size = new System.Drawing.Size(75, 23);
                this.cancelButton.TabIndex = 2;
                this.cancelButton.Text = "Cancel";
                this.cancelButton.UseVisualStyleBackColor = true;
                this.cancelButton.Click += new System.EventHandler(this.CancelButton_Click);
                // Gemini_RAG
                this.AcceptButton = this.okButton;
                this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
                this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
                this.CancelButton = this.cancelButton;
                this.ClientSize = new System.Drawing.Size(484, 191); // Adjusted size
                this.Controls.Add(this.cancelButton);
                this.Controls.Add(this.okButton);
                this.Controls.Add(this.promptTextBox);
                this.MaximizeBox = false;
                this.MinimizeBox = false;
                this.MinimumSize = new System.Drawing.Size(300, 150); // Allow some resizing
                this.Name = "Gemini_RAG";
                this.ShowIcon = false;
                this.ShowInTaskbar = false;
                this.StartPosition = System.Windows.Forms.FormStartPosition.CenterParent;
                this.Text = "Enter Prompt for AI Assistant";
                this.ResumeLayout(false);
                this.PerformLayout();
            }

            private void OkButton_Click(object sender, EventArgs e)
            {
                this.UserPrompt = this.promptTextBox.Text;
                this.DialogResult = DialogResult.OK;
                this.Close();
            }

            private void CancelButton_Click(object sender, EventArgs e)
            {
                this.DialogResult = DialogResult.Cancel;
                this.Close();
            }
        }
    }
} // End Namespace