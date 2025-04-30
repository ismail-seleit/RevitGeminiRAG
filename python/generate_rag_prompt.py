# -*- coding: utf-8 -*-
import chromadb
from chromadb.utils import embedding_functions
# <<< --- IMPORT SentenceTransformer and util --- >>>
from sentence_transformers import SentenceTransformer, util
import os
import sys
import argparse
import traceback
import logging
import torch
import json
import pprint
import re # For parsing filename
import google.generativeai as genai

# --- Configuration ---
persist_directory = r"C:\Users\isele\Documents\RevitAPI_2025\revit_db_arctic"
collection_name = "revit_api_2025_arctic_l_refined_v3"
model_name = 'Snowflake/snowflake-arctic-embed-l-v2.0' # Model for RAG embeddings AND few-shot similarity

# <<< --- ADDED: Configuration for Successful Scripts & Few-Shot --- >>>
successful_scripts_directory = r"C:\ProgramData\Autodesk\Revit\Addins\2025\GeneratedSuccessfulCode" # <<<--- IMPORTANT: SET THIS PATH
num_few_shot_examples_to_select = 2 # How many dynamic examples to inject
# <<< --- END ADDED CONFIGURATION --- >>>

GEMINI_MODEL_NAME = 'gemini-2.0-flash' # Or your preferred Gemini model
google_api_key = os.environ.get("GOOGLE_API_KEY") # Get API key once

num_results_per_query = 7
final_num_results = 15 # RAG results (separate from few-shot)

transformer_device = 'cuda' if torch.cuda.is_available() else 'cpu'

# --- File Logging Setup ---
try:
    log_file_path = os.path.join(os.path.expanduser("~"), "Documents", "RevitGeminiRAG_Log.txt")
    for handler in logging.root.handlers[:]: logging.root.removeHandler(handler)
    logging.basicConfig(level=logging.DEBUG,
                        format='%(asctime)s - %(levelname)s - %(message)s',
                        filename=log_file_path,
                        filemode='a')
    logging.info(f"--- Python RAG Script Started (Gemini Refinement + RAG + Dynamic Few-Shot) ---")
except Exception as log_setup_ex:
    print(f"PYTHON_ERROR: Failed to configure file logging: {log_setup_ex}", file=sys.stderr)
    logging = None

# --- Logging Functions (Unchanged) ---
def log_error(message):
    print(f"PYTHON_ERROR: {message}", file=sys.stderr)
    print(f"PYTHON_TRACEBACK:\n{traceback.format_exc()}", file=sys.stderr)
    if logging: logging.error(message, exc_info=True)

def log_debug(message):
    print(f"PYTHON_DEBUG: {message}", file=sys.stderr)
    if logging: logging.debug(message)

# --- Gemini Query Refinement Function (Unchanged - Assuming it works well) ---
def refine_query_with_gemini(original_query, api_key):
    # ... (Keep your existing refine_query_with_gemini function here) ...
    # --- (Ensure it handles fallbacks gracefully if Gemini fails or API key is missing) ---
    log_debug(f"Refining query with Gemini ({GEMINI_MODEL_NAME}): '{original_query}'")
    if not api_key:
        log_debug("GOOGLE_API_KEY is not set. Cannot use Gemini for refinement. Using original query.")
        return [original_query] # Fallback to original query

    try:
        genai.configure(api_key=api_key)
        # Use safety settings similar to your C# code if needed
        # safety_settings=[...]
        model = genai.GenerativeModel(GEMINI_MODEL_NAME)

        # Construct the prompt for Gemini
        gemini_prompt = f"""
        You are an expert in the Autodesk Revit API. Your task is to refine a user's query to make it more effective for searching technical Revit API documentation using vector similarity (RAG).

        Rephrase the following user query into one or more specific, technical search terms. Focus on using precise Revit API class names (e.g., FilteredElementCollector, Wall, Floor, Parameter, OverrideGraphicSettings), method names (e.g., Create.NewFloor, SetElementOverrides), properties (e.g., HOST_AREA_COMPUTED, BuiltInParameter.WALL_USER_HEIGHT_PARAM), and common concepts used in the Revit API.

        The goal is to create queries that will match relevant code examples or documentation snippets about the Revit API, likely written in C# or Python for pyRevit/RevitPythonShell.

        If the query is already quite specific and technical, you can return it as is within the list. If it's vague (e.g., "make a wall"), make it more concrete (e.g., "Revit API create Wall element", "Wall.Create method example"). If it mentions UI actions (e.g., "click the wall tool"), translate it to the API equivalent (e.g., "programmatically create Wall Revit API").

        Return the result ONLY as a JSON list of strings. Do not include any explanations or markdown formatting outside the JSON list itself.

        Example Input: "how to get wall areas in the current view"
        Example Output:
        [
          "Revit API Wall Area Parameter HOST_AREA_COMPUTED",
          "FilteredElementCollector get Wall area in view",
          "Calculate area for Wall elements active view API",
          "Iterate Walls get BuiltInParameter HOST_AREA_COMPUTED example",
          "Wall element area property view filter API"
        ]

        Example Input: "change the color of selected elements"
        Example Output:
        [
            "Revit API OverrideGraphicSettings SetProjectionColor",
            "Change element color in view C# example API",
            "Override element graphics color API",
            "View SetElementOverrides element color",
            "Autodesk.Revit.DB.OverrideGraphicSettings color change method"
        ]

        Example Input: "create a floor using lines"
        Example Output:
        [
            "Revit API Floor Create method CurveLoop",
            "Document.Create.NewFloor example C# CurveLoop",
            "Create Floor element using CurveLoop profile API",
            "NewFloor(Document, CurveLoop, FloorType, Level)",
            "Generate Floor geometry from lines Revit API"
        ]

        Original User Query:
        "{original_query}"

        Refined JSON List:
        """

        response = model.generate_content(gemini_prompt)

        cleaned_response_text = response.text.strip()
        if cleaned_response_text.startswith("```json"):
            cleaned_response_text = cleaned_response_text[7:]
        if cleaned_response_text.endswith("```"):
            cleaned_response_text = cleaned_response_text[:-3]
        cleaned_response_text = cleaned_response_text.strip()

        refined_queries = json.loads(cleaned_response_text)

        if isinstance(refined_queries, list) and all(isinstance(q, str) and q.strip() for q in refined_queries) and refined_queries:
            log_debug(f"Gemini returned refined queries: {refined_queries}")
            return refined_queries
        else:
            log_error(f"Gemini response was not a valid JSON list of non-empty strings: {cleaned_response_text}")
            return [original_query] # Fallback

    except json.JSONDecodeError as e:
        log_error(f"Error decoding Gemini JSON response: {e}. Raw response was likely: '{response.text if 'response' in locals() else 'N/A'}'")
        return [original_query] # Fallback
    except Exception as e:
        log_error(f"Error during Gemini query refinement: {e}")
        return [original_query] # Fallback

# <<< --- ADDED: Function to Load Successful Script Metadata --- >>>
def load_successful_script_metadata(directory):
    """Scans the directory for .py files matching the naming convention and extracts metadata."""
    script_metadata = []
    if not os.path.isdir(directory):
        log_debug(f"Warning: Successful scripts directory not found or not a directory: {directory}")
        return script_metadata

    # Regex to capture the prompt part and timestamp (adjust if your format differs slightly)
    # Assumes format: Prompt_Part_YYYYMMDD_HHMMSS.py
    # It captures the part before the first YYYYMMDD sequence
    # filename_regex = re.compile(r"^(.*?)_(\d{8}_\d{6})\.py$")
    filename_regex = re.compile(r"^(.*?)_(\d{8}_\d{6})\.py$", re.IGNORECASE)


    log_debug(f"Scanning for successful scripts in: {directory}")
    try:
        for filename in os.listdir(directory):
            filepath = os.path.join(directory, filename)
            if filename.lower().endswith(".py") and os.path.isfile(filepath):
                match = filename_regex.match(filename)
                if match:
                    sanitized_prompt = match.group(1)
                    timestamp = match.group(2)
                    # Try to "un-sanitize" the prompt (basic: replace underscore with space)
                    original_prompt_guess = sanitized_prompt.replace('_', ' ').strip()
                    if original_prompt_guess: # Ensure we have a non-empty prompt guess
                         script_metadata.append({
                             "filepath": filepath,
                             "filename": filename,
                             "original_prompt": original_prompt_guess,
                             "timestamp": timestamp
                         })
                    else:
                         log_debug(f"Skipping file {filename}: Could not extract a valid prompt part.")
                else:
                     log_debug(f"Skipping file {filename}: Does not match expected naming format.")
            else:
                 log_debug(f"Skipping non-matching file or directory: {filename}")

    except Exception as e:
        log_error(f"Error scanning successful scripts directory '{directory}': {e}")

    log_debug(f"Found {len(script_metadata)} potential successful script examples.")
    return script_metadata
# <<< --- END ADDED FUNCTION --- >>>


# --- Main Script Logic ---
if __name__ == "__main__":
    # --- 0. Argument Parsing & Initial Setup ---
    parser = argparse.ArgumentParser(description='Generate LLM prompt using Gemini refinement, RAG, and dynamic few-shot examples.')
    parser.add_argument('query', type=str, help='The user query/question for the Revit API.')

    original_query_text = None
    client = None
    collection = None
    successful_scripts = []
    sentence_model = None # Initialize sentence model variable

    try:
        args = parser.parse_args()
        original_query_text = args.query
        log_debug(f"Received original query: {original_query_text}")
        if not original_query_text or not original_query_text.strip():
             log_error("Original query text cannot be empty."); sys.exit(1)

        # <<< --- Load Successful Script Metadata EARLY --- >>>
        successful_scripts = load_successful_script_metadata(successful_scripts_directory)

        # <<< --- Instantiate Sentence Transformer Model ONCE --- >>>
        # We need it for both few-shot similarity and potentially later for RAG if EF wasn't used
        log_debug(f"Loading Sentence Transformer model: {model_name} on device: {transformer_device}")
        sentence_model = SentenceTransformer(model_name, device=transformer_device, trust_remote_code=True) # trust_remote_code often needed
        log_debug("Sentence Transformer model loaded.")

        # Log other config details (unchanged)
        log_debug(f"Using ChromaDB path: {os.path.abspath(persist_directory)}")
        log_debug(f"Using collection: {collection_name}")
        log_debug(f"Retrieving {num_results_per_query} RAG results per refined query, aiming for {final_num_results} final RAG results.")
        log_debug(f"Attempting to select {num_few_shot_examples_to_select} dynamic few-shot examples.")

    except Exception as e: log_error(f"Error during initial setup or argument parsing: {e}"); sys.exit(1)


    # --- 1. Refine Query with Gemini ---
    refined_queries = refine_query_with_gemini(original_query_text, google_api_key)
    log_debug(f"Using queries for RAG retrieval: {refined_queries}")


    # --- 2. Select Dynamic Few-Shot Examples ---
    selected_few_shot_examples_formatted = ""
    if successful_scripts and sentence_model and num_few_shot_examples_to_select > 0:
        log_debug("Selecting dynamic few-shot examples based on similarity...")
        try:
            # Embed the current user query
            current_query_embedding = sentence_model.encode([original_query_text], convert_to_tensor=True, device=transformer_device)

            # Embed the original prompts from the successful scripts
            example_prompts = [script['original_prompt'] for script in successful_scripts]
            example_prompt_embeddings = sentence_model.encode(example_prompts, convert_to_tensor=True, device=transformer_device)

            # Calculate Cosine Similarity
            cosine_scores = util.cos_sim(current_query_embedding, example_prompt_embeddings)[0] # Get scores for the single current query

            # Get top N indices (add check to ensure we don't ask for more than available)
            num_to_get = min(num_few_shot_examples_to_select, len(successful_scripts))
            top_results = torch.topk(cosine_scores, k=num_to_get)

            log_debug(f"Top {num_to_get} few-shot example candidates (score | index | prompt):")
            selected_examples_parts = []
            for i in range(num_to_get):
                score = top_results.values[i].item()
                idx = top_results.indices[i].item()
                selected_script_meta = successful_scripts[idx]
                log_debug(f"  - {score:.4f} | {idx} | '{selected_script_meta['original_prompt']}' ({selected_script_meta['filename']})")

                # Read the selected script's code
                try:
                    with open(selected_script_meta['filepath'], 'r', encoding='utf-8') as f:
                        script_code = f.read()

                    # Format the example for the prompt
                    example_part = f"--- Start Dynamic Example {i+1} ---\n"
                    example_part += f"USER QUESTION EXAMPLE:\n---\n{selected_script_meta['original_prompt']}\n---\n\n" # Use the parsed prompt
                    example_part += f"PYTHON SCRIPT EXAMPLE:\n```python\n{script_code}\n```\n"
                    example_part += f"--- End Dynamic Example {i+1} ---"
                    selected_examples_parts.append(example_part)

                except Exception as read_ex:
                    log_error(f"Failed to read selected few-shot script '{selected_script_meta['filepath']}': {read_ex}")
                    # Optionally: continue to next best, or just skip this one

            selected_few_shot_examples_formatted = "\n\n".join(selected_examples_parts)

        except Exception as few_shot_ex:
            log_error(f"Error during dynamic few-shot example selection: {few_shot_ex}")
            selected_few_shot_examples_formatted = "# Error selecting dynamic few-shot examples." # Placeholder indicating failure

    elif not successful_scripts:
         log_debug("Skipping dynamic few-shot selection: No successful script metadata loaded.")
         selected_few_shot_examples_formatted = "# No successful script examples were found to include."
    else:
         log_debug(f"Skipping dynamic few-shot selection: num_few_shot_examples_to_select is {num_few_shot_examples_to_select}.")
         selected_few_shot_examples_formatted = "# Dynamic few-shot examples disabled by configuration."


    # --- 3. Connect to ChromaDB and Perform RAG Query ---
    context_documents = [] # Initialize RAG context
    if not os.path.isdir(persist_directory):
        log_error(f"ChromaDB directory not found at: {os.path.abspath(persist_directory)}"); # sys.exit(1) - Maybe don't exit, try to proceed without RAG? Or handle later.
    else:
        try:
            log_debug(f"Connecting to ChromaDB at: {persist_directory}")
            client = chromadb.PersistentClient(path=persist_directory)

            # Define EF *inside* the try block for ChromaDB connection
            embedding_function = embedding_functions.SentenceTransformerEmbeddingFunction(
                model_name=model_name, device=transformer_device, trust_remote_code=True
            )
            log_debug(f"Getting collection: {collection_name} with EF: {model_name}")
            collection = client.get_collection(name=collection_name, embedding_function=embedding_function)
            log_debug(f"Connected to collection '{collection_name}'. Count: {collection.count()}")

            # --- 4. Query ChromaDB with Refined Queries (RAG part) ---
            log_debug(f"Querying ChromaDB for RAG context with {len(refined_queries)} refined queries...")
            results = collection.query(
                query_texts=refined_queries,
                n_results=num_results_per_query,
                include=['metadatas', 'documents', 'distances']
            )

            # --- 5. Combine, De-duplicate, and Rank RAG Results ---
            all_results_dict = {}
            log_debug("Combining and de-duplicating RAG results...")
            # ... (Your existing result processing logic - keep the version from the previous step
            #      that handles potential metadata differences if you indexed scripts there too) ...
            # --- (Ensure this logic correctly extracts 'document' from the RAG results) ---
            if results and results.get('ids'):
                for i in range(len(results['ids'])): # Index corresponds to refined_queries[i]
                    if results['ids'][i] is None or not results['ids'][i]:
                        # log_debug(f"No RAG results found for refined query {i+1}: '{refined_queries[i]}'")
                        continue

                    if (results.get('documents') is None or len(results['documents']) <= i or results['documents'][i] is None or
                        results.get('metadatas') is None or len(results['metadatas']) <= i or results['metadatas'][i] is None or
                        results.get('distances') is None or len(results['distances']) <= i or results['distances'][i] is None or
                        not (len(results['ids'][i]) == len(results['documents'][i]) == len(results['metadatas'][i]) == len(results['distances'][i]))):
                        log_error(f"Inconsistent RAG result data structure for query {i+1}. Skipping.")
                        continue

                    query_ids = results['ids'][i]
                    query_docs = results['documents'][i]
                    query_metas = results['metadatas'][i]
                    query_dists = results['distances'][i]

                    for j in range(len(query_ids)):
                        doc_id = query_ids[j]
                        distance = query_dists[j]
                        document = query_docs[j]
                        metadata = query_metas[j]

                        if not doc_id or document is None or metadata is None or distance is None:
                            # log_debug(f"Skipping invalid RAG result entry (ID: {doc_id}) for query {i+1}.")
                            continue

                        if doc_id not in all_results_dict or distance < all_results_dict[doc_id]['distance']:
                            all_results_dict[doc_id] = {
                                'document': document,
                                'metadata': metadata,
                                'distance': distance,
                                'id': doc_id
                            }

                log_debug(f"Found {len(all_results_dict)} unique RAG results from refined queries.")
                sorted_results = sorted(all_results_dict.values(), key=lambda item: item['distance'])
                top_results = sorted_results[:final_num_results]
                log_debug(f"Selected top {len(top_results)} RAG results after ranking.")
                context_documents = [res['document'] for res in top_results] # Just get the text content for RAG

                # Optional: Log RAG results details (simplified logging)
                # for k, res in enumerate(top_results):
                #     log_debug(f"  RAG Result {k+1}: ID={res.get('id','N/A')} | Dist={res.get('distance', -1):.4f}")

            else:
                log_debug("Warning: No relevant RAG documents found in ChromaDB.")
                context_documents = []

        except Exception as e:
            log_error(f"Error querying ChromaDB or processing RAG results: {e}")
            context_documents = [] # Ensure context_documents is empty on error

    # --- 6. Construct the Final Prompt ---
    log_debug("Constructing final prompt for code generation LLM...")
    context_string = "\n\n---\n\n".join(context_documents)

    # <<< *** UPDATED FINAL PROMPT TEMPLATE with Dynamic Few-Shot Placeholder and EXCEL format support *** >>>
    prompt_template = """ROLE: You are an expert Revit API assistant generating Python code.

TASK: Generate Python code only, suitable for direct execution in Revit Python Shell or pyRevit using IronPython. Follow the format demonstrated in the examples below.

RESPONSE FORMAT:
- Output ONLY Python code.
- Start directly with imports or executable code.
- Do NOT include ```python``` markdown, explanations, or any surrounding text.

EXECUTION ENVIRONMENT:
- The script will run within an existing Revit Transaction managed by C# code.
- Assume these variables are PRE-DEFINED in the execution scope:
    - `doc`: The current Autodesk.Revit.DB.Document.
    - `uidoc`: The current Autodesk.Revit.UI.UIDocument.
    - `app`: The current Autodesk.Revit.ApplicationServices.Application.
    - `uiapp`: The current Autodesk.Revit.UI.UIApplication.

CRITICAL CONSTRAINTS:
- DO NOT manage Revit Transactions (NO `Transaction()`, `t.Start()`, `t.Commit()`). The C# wrapper handles this. Write only the core API calls.
- DO NOT generate code that requires user interaction (e.g., selecting files, showing dialogs). The entire operation must be driven by the initial prompt.

CODE QUALITY & IMPORT REQUIREMENTS:
- **Mandatory Imports:** Ensure ALL required classes from `Autodesk.Revit.DB` and necessary .NET types are imported explicitly at the start using `from Autodesk.Revit.DB import ...`. Assume standard Revit API assemblies are referenced, but use `clr.AddReference()` if needed. Missing imports are a common failure point.
- **Syntactic Correctness:** Write syntactically correct Python code valid for IronPython. Aim for code that runs without syntax errors.
- **Leverage Examples:** Pay close attention to the DYNAMIC FEW-SHOT EXAMPLES and STATIC EXAMPLES provided below. These demonstrate correct syntax and patterns. Prioritize these examples when relevant.
- **Context First:** Also consider the patterns found in the RAG CONTEXT FROM DOCUMENTATION section provided below.
- **Units:** Be mindful of Revit's internal units (decimal feet).
- **Ambiguity:** If the request is ambiguous, add Python comments (`#`) explaining assumptions.
- **Impossible Tasks:** If impossible via API, output ONLY `# Error: [Reason]`.

DATA EXPORT HANDLING:
- If the user request asks to EXPORT or SAVE data:
    1. Collect and format the data as a single string (e.g., CSV lines with '\n').
    2. PRINT the output in the specific format:
       ```
       EXPORT::[FORMAT]::[FILENAME_SUGGESTION]
       [DATA_CONTENT_STRING]
       ```
       (Replace FORMAT with CSV/TXT/EXCEL, FILENAME_SUGGESTION appropriately. Ensure a newline separates header and data.)
       For EXCEL exports, use the same CSV format for data but specify EXCEL as the format.
    3. Do NOT print anything else if exporting data.

--- DYNAMIC FEW-SHOT EXAMPLES (Most Relevant to Current Query) ---
{dynamic_examples_placeholder}
--- END DYNAMIC FEW-SHOT EXAMPLES ---


--- STATIC EXAMPLES (General Formatting and Common Tasks) ---

--- EXAMPLE 1 START (Modify Elements) ---
# ... (Your Example 1 from previous version) ...
USER QUESTION EXAMPLE:
---
select all walls thicker than 6 inches in view
---

PYTHON SCRIPT EXAMPLE:
# Import necessary classes
import clr
clr.AddReference('System.Collections') # Required for List<T>
from Autodesk.Revit.DB import FilteredElementCollector, BuiltInCategory, Wall, ElementId
from System.Collections.Generic import List

# Define the thickness threshold in feet (6 inches = 0.5 feet)
min_thickness_feet = 0.5

# Get the active view ID, handle potential errors if no active view
try:
    active_view_id = doc.ActiveView.Id
except AttributeError:
    print("# Error: Could not get active view ID. Cannot filter by view.")
    active_view_id = ElementId.InvalidElementId

walls_to_select_ids = []
if active_view_id != ElementId.InvalidElementId:
    collector = FilteredElementCollector(doc, active_view_id)
    wall_collector = collector.OfCategory(BuiltInCategory.OST_Walls).WhereElementIsNotElementType()
    for wall in wall_collector:
        if isinstance(wall, Wall):
            try:
                wall_thickness = wall.Width
                if wall_thickness > min_thickness_feet:
                    walls_to_select_ids.append(wall.Id)
            except Exception as e:
                # print(f"# Debug: Skipping element {{wall.Id}}, could not get Width. Error: {{e}}") # Escaped
                pass # Silently skip walls where Width cannot be accessed

selection_list = List[ElementId](walls_to_select_ids)
try:
    uidoc.Selection.SetElementIds(selection_list)
    # print(f"# Selected {{len(walls_to_select_ids)}} walls.") # Escaped Optional output
except Exception as sel_ex:
    print(f"# Error setting selection: {{sel_ex}}") # Escaped

--- EXAMPLE 1 END ---

--- EXAMPLE 2 START (Export Data to CSV) ---
# ... (Your Example 2 from previous version) ...
USER QUESTION EXAMPLE:
---
export the name and area of all floors to a csv file
---

PYTHON SCRIPT EXAMPLE:
# Import necessary classes
import clr
clr.AddReference('RevitAPI') # Assumed standard ref
clr.AddReference('RevitAPIUI') # Assumed standard ref
# Explicit DB imports are key
from Autodesk.Revit.DB import FilteredElementCollector, BuiltInCategory, Floor, UnitUtils, DisplayUnitType, BuiltInParameter
from System.Collections.Generic import List # Example of needing .NET list

# List to hold CSV lines
csv_lines = []
# Add header row
csv_lines.append("Floor Name,Area (sq ft)") # Example header

# Collect all Floor elements
collector = FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Floors).WhereElementIsNotElementType()

# Iterate through floors and get data
for floor in collector:
    if isinstance(floor, Floor):
        try:
            name = floor.Name
            # Get area parameter (HOST_AREA_COMPUTED is common for floors)
            area_param = floor.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED)
            if area_param:
                area_value_internal = area_param.AsDouble()
                area_str = "{{:.2f}}".format(area_value_internal) # Escaped format specifier
            else:
                area_str = "N/A"

            # Escape commas in name if necessary (basic CSV handling)
            safe_name = '"' + name.replace('"', '""') + '"'
            csv_lines.append(f"{{safe_name}},{{area_str}}") # Escaped f-string variables
        except Exception as e:
            pass # Skip floors that cause errors

# Check if we gathered any data
if len(csv_lines) > 1: # More than just the header
    # Format the final output for export
    file_content = "\\n".join(csv_lines)
    print("EXPORT::CSV::floor_areas.csv") # <-- The marker line
    print(file_content)                 # <-- The data content string
else:
    print("# No floor elements found or processed.")

--- EXAMPLE 2 END ---

--- EXAMPLE 3 START (Export Data to TXT) ---
# ... (Your Example 3 from previous version) ...
USER QUESTION EXAMPLE:
---
list all view names and their view type in a text file
---

PYTHON SCRIPT EXAMPLE:
# Import necessary classes
from Autodesk.Revit.DB import FilteredElementCollector, View

# List to hold text lines
text_lines = []
text_lines.append("Revit Views Report")
text_lines.append("==================")

# Collect all View elements
collector = FilteredElementCollector(doc).OfClass(View)

# Iterate through views and get data
for view in collector:
    if isinstance(view, View):
        try:
            name = view.Name
            view_type_enum = view.ViewType # Get the enum value
            view_type = view_type_enum.ToString() # Convert enum to string
            text_lines.append(f"Name: {{name}} | Type: {{view_type}}") # Escaped f-string variables
        except Exception as e:
            pass # Skip views that cause errors

# Check if we gathered any data
if len(text_lines) > 2: # More than just the header lines
    # Format the final output for export
    file_content = "\\n".join(text_lines)
    print("EXPORT::TXT::view_list.txt") # <-- The marker line
    print(file_content)                # <-- The data content string
else:
    print("# No view elements found or processed.")

--- EXAMPLE 3 END ---

--- EXAMPLE 4 START (Export Data to Excel) ---
USER QUESTION EXAMPLE:
---
export wall data to excel
---

PYTHON SCRIPT EXAMPLE:
# Import necessary classes
import clr
from Autodesk.Revit.DB import FilteredElementCollector, BuiltInCategory, Wall, BuiltInParameter

# List to hold CSV lines (Excel format uses CSV data with EXCEL marker)
csv_lines = []
# Add header row
csv_lines.append("Wall ID,Family,Type,Length,Height")

# Collect all Wall elements
collector = FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Walls).WhereElementIsNotElementType()

# Iterate through walls and get data
for wall in collector:
    if isinstance(wall, Wall):
        try:
            wall_id = wall.Id.IntegerValue
            family_param = wall.get_Parameter(BuiltInParameter.ELEM_FAMILY_PARAM)
            family_name = family_param.AsValueString() if family_param else "N/A"
            type_param = wall.get_Parameter(BuiltInParameter.ELEM_TYPE_PARAM)
            type_name = type_param.AsValueString() if type_param else "N/A"
            length_param = wall.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH)
            length = length_param.AsDouble() if length_param else 0
            height_param = wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM)
            height = height_param.AsDouble() if height_param else 0
            
            # Format the row with quoted strings to handle commas
            safe_family = f'"{{family_name}}"' if ',' in family_name else family_name
            safe_type = f'"{{type_name}}"' if ',' in type_name else type_name
            csv_lines.append(f"{{wall_id}},{{safe_family}},{{safe_type}},{{length:.2f}},{{height:.2f}}")
        except Exception as e:
            pass # Skip walls that cause errors

# Check if we gathered any data
if len(csv_lines) > 1: # More than just the header
    # Format the final output for export as Excel
    file_content = "\n".join(csv_lines)
    print("EXPORT::EXCEL::wall_data.xlsx") # <-- Note EXCEL format specified
    print(file_content)                    # <-- The data content string (same CSV format)
else:
    print("# No wall elements found or processed.")

--- EXAMPLE 4 END ---

--- END STATIC EXAMPLES ---


--- RAG CONTEXT FROM DOCUMENTATION (Less reliable than examples) ---
{context_placeholder}
--- END RAG CONTEXT ---


USER QUESTION:
---
{query_placeholder}
---

PYTHON SCRIPT:
""" # End of the prompt_template definition

    try:
        # Validate placeholders
        required_placeholders = ['{dynamic_examples_placeholder}', '{context_placeholder}', '{query_placeholder}']
        if not all(p in prompt_template for p in required_placeholders):
             missing = [p for p in required_placeholders if p not in prompt_template]
             log_error(f"Prompt template is missing required placeholders: {missing}"); sys.exit(1)

        # Escape braces in dynamic content BEFORE formatting the main template
        safe_dynamic_examples = selected_few_shot_examples_formatted.replace('{', '{{').replace('}', '}}')
        safe_context_string = context_string.replace('{', '{{').replace('}', '}}')
        safe_query_placeholder = original_query_text.replace('{', '{{').replace('}', '}}')

        # Use the escaped strings in the format call
        prompt_for_llm = prompt_template.format(
            dynamic_examples_placeholder=safe_dynamic_examples,
            context_placeholder=(safe_context_string if context_string else "# No relevant documentation snippets found."),
            query_placeholder=safe_query_placeholder
        )

    except KeyError as key_err:
         log_error(f"Error formatting the prompt string: Missing key {key_err}. Check template placeholders."); sys.exit(1)
    except Exception as fmt_ex:
        log_error(f"Error formatting the prompt string: {fmt_ex}"); sys.exit(1)

    # --- 7. Output the Final Prompt ---
    try:
        output_bytes = prompt_for_llm.encode('utf-8')
        sys.stdout.buffer.write(output_bytes)
        sys.stdout.flush()
    except Exception as write_ex:
        log_error(f"Error writing prompt to stdout: {write_ex}")
        print(prompt_for_llm) # Fallback

    log_debug("Successfully generated and printed final LLM prompt to stdout.")
    if logging: logging.info("--- Python RAG Script Finished Successfully ---")
    sys.exit(0)

# --- Error Exit ---
if logging: logging.error("--- Python RAG Script Exited with Error ---")
# sys.exit(1) would have already been called if termination was needed.