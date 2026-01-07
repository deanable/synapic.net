import customtkinter as ctk
import threading
import sys
from youtube_client import YouTubeClient
from ai_optimizer import AIOptimizer

ctk.set_appearance_mode("System")
ctk.set_default_color_theme("blue")

class App(ctk.CTk):
    def __init__(self):
        super().__init__()

        self.title("AI YouTube Optimiser")
        self.geometry("900x700")

        # Layout Setup
        self.grid_columnconfigure(0, weight=1)
        self.grid_rowconfigure(3, weight=1) # Description
        self.grid_rowconfigure(5, weight=1) # Chapters

        # Header
        self.header_frame = ctk.CTkFrame(self)
        self.header_frame.grid(row=0, column=0, padx=20, pady=(20, 10), sticky="ew")
        
        self.label_video_id = ctk.CTkLabel(self.header_frame, text="YouTube Video ID:")
        self.label_video_id.pack(side="left", padx=10)
        
        self.entry_video_id = ctk.CTkEntry(self.header_frame, width=300, placeholder_text="e.g. dQw4w9WgXcQ")
        self.entry_video_id.pack(side="left", padx=10)
        
        self.btn_optimize = ctk.CTkButton(self.header_frame, text="Optimize", command=self.start_optimization)
        self.btn_optimize.pack(side="left", padx=10)

        self.lbl_status = ctk.CTkLabel(self.header_frame, text="Ready", text_color="gray")
        self.lbl_status.pack(side="left", padx=10)

        # 1. Titles
        self.lbl_titles = ctk.CTkLabel(self, text="Optimized Titles", font=("Arial", 16, "bold"), anchor="w")
        self.lbl_titles.grid(row=1, column=0, padx=20, pady=(10, 0), sticky="w")
        self.txt_titles = ctk.CTkTextbox(self, height=100)
        self.txt_titles.grid(row=2, column=0, padx=20, pady=(5, 10), sticky="ew")

        # 2. Description
        self.lbl_description = ctk.CTkLabel(self, text="Optimized Description", font=("Arial", 16, "bold"), anchor="w")
        self.lbl_description.grid(row=3, column=0, padx=20, pady=(10, 0), sticky="w")
        self.txt_description = ctk.CTkTextbox(self, height=200)
        self.txt_description.grid(row=4, column=0, padx=20, pady=(5, 10), sticky="nsew")

        # 3. Chapters
        self.lbl_chapters = ctk.CTkLabel(self, text="Suggested Chapters", font=("Arial", 16, "bold"), anchor="w")
        self.lbl_chapters.grid(row=5, column=0, padx=20, pady=(10, 0), sticky="w")
        self.txt_chapters = ctk.CTkTextbox(self, height=150)
        self.txt_chapters.grid(row=6, column=0, padx=20, pady=(5, 20), sticky="ew")

        self.youtube_client = YouTubeClient()
        self.ai_optimizer = AIOptimizer()

    def update_status(self, text):
        self.lbl_status.configure(text=text)

    def start_optimization(self):
        video_id = self.entry_video_id.get().strip()
        if not video_id:
            self.update_status("Error: Please enter a Video ID")
            return

        self.btn_optimize.configure(state="disabled")
        self.update_status("Starting...")
        
        # Run in a separate thread
        thread = threading.Thread(target=self.run_optimization_process, args=(video_id,))
        thread.start()

    def run_optimization_process(self, video_id):
        try:
            # Authenticate
            self.update_status("Authenticating...")
            try:
                self.youtube_client.authenticate()
            except Exception as e:
                self.update_status(f"Auth Error: {e}")
                self.btn_optimize.configure(state="normal")
                return

            # Fetch Data
            self.update_status("Fetching Video Details...")
            video_details = self.youtube_client.get_video_details(video_id)
            if not video_details:
                self.update_status("Error: Video not found")
                self.btn_optimize.configure(state="normal")
                return

            self.update_status("Fetching Transcript...")
            transcript = self.youtube_client.get_transcript(video_id)
            if not transcript:
                transcript = "" # Handled by Optimizer logic or warning
                print("No transcript found, using metadata only.")

            # Optimize
            self.update_status("Running AI Optimization... (this may take a moment)")
            result = self.ai_optimizer.generate_optimization(
                title=video_details['title'],
                description=video_details['description'],
                transcript=transcript
            )

            if not result:
                self.update_status("Error: AI Optimization failed")
            else:
                self.update_status("Done!")
                self.display_results(result)

        except Exception as e:
            self.update_status(f"Error: {str(e)}")
        finally:
            self.btn_optimize.configure(state="normal")

    def display_results(self, result):
        # Update UI elements in a thread-safe way is usually recommended, 
        # but CTK methods often handle it. If issues arise, use .after().
        
        # Titles
        titles_text = "\n".join([f"{i+1}. {t}" for i, t in enumerate(result.get('optimized_titles', []))])
        self.txt_titles.delete("1.0", "end")
        self.txt_titles.insert("1.0", titles_text)

        # Description
        desc_text = result.get('optimized_description', '')
        self.txt_description.delete("1.0", "end")
        self.txt_description.insert("1.0", desc_text)

        # Chapters
        chapters_text = "\n".join([f"{c['timestamp']} - {c['title']}" for c in result.get('chapters', [])])
        self.txt_chapters.delete("1.0", "end")
        self.txt_chapters.insert("1.0", chapters_text)


if __name__ == "__main__":
    app = App()
    app.mainloop()
