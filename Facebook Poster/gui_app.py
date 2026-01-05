import customtkinter as ctk
import threading
import sys
import time
import schedule
import tkinter as tk
from tkinter import messagebox
from datetime import datetime

# Import existing logic
from scheduler_job import JobManager
from auth import FacebookAuth
from config import Config

# --- Logger Class to redirect stdout to GUI --- #
class PrintLogger:
    def __init__(self, text_widget):
        self.text_widget = text_widget
        self.terminal = sys.stdout

    def write(self, message):
        self.terminal.write(message)  # Write to original stdout
        # Update GUI in a thread-safe way
        def append():
            self.text_widget.configure(state="normal")
            self.text_widget.insert("end", message)
            self.text_widget.see("end")
            self.text_widget.configure(state="disabled")
        
        # Check if the widget exists and called from main thread or use after
        if self.text_widget.winfo_exists():
            self.text_widget.after(0, append)

    def flush(self):
        self.terminal.flush()

# --- Main App Class --- #
class App(ctk.CTk):
    def __init__(self):
        super().__init__()

        self.title("Facebook Auto-Poster")
        self.geometry("900x600")
        
        # Config
        ctk.set_appearance_mode("Dark")
        ctk.set_default_color_theme("blue")

        # Data
        self.job_manager = JobManager()
        self.scheduler_running = False
        self.scheduler_thread = None

        # Layout
        self.grid_columnconfigure(1, weight=1)
        self.grid_rowconfigure(0, weight=1)

        self.create_sidebar()
        self.create_dashboard()
        self.create_queue_page()
        self.create_settings_page()

        # Redirect stdout
        self.logger = PrintLogger(self.log_box)
        sys.stdout = self.logger
        sys.stderr = self.logger # Also capture errors

        # Start with Dashboard
        self.select_frame_by_name("dashboard")

    def create_sidebar(self):
        self.sidebar_frame = ctk.CTkFrame(self, width=140, corner_radius=0)
        self.sidebar_frame.grid(row=0, column=0, rowspan=4, sticky="nsew")
        self.sidebar_frame.grid_rowconfigure(4, weight=1)

        self.logo_label = ctk.CTkLabel(self.sidebar_frame, text="FB Poster", font=ctk.CTkFont(size=20, weight="bold"))
        self.logo_label.grid(row=0, column=0, padx=20, pady=(20, 10))

        self.sidebar_button_1 = ctk.CTkButton(self.sidebar_frame, text="Dashboard", command=self.sidebar_button_event)
        self.sidebar_button_1.grid(row=1, column=0, padx=20, pady=10)

        self.sidebar_button_2 = ctk.CTkButton(self.sidebar_frame, text="Queue", command=self.sidebar_button_event)
        self.sidebar_button_2.grid(row=2, column=0, padx=20, pady=10)

        self.sidebar_button_3 = ctk.CTkButton(self.sidebar_frame, text="Settings", command=self.sidebar_button_event)
        self.sidebar_button_3.grid(row=3, column=0, padx=20, pady=10)
        
        # Store button references to change colors
        self.sidebar_buttons = [self.sidebar_button_1, self.sidebar_button_2, self.sidebar_button_3]

    def create_dashboard(self):
        self.dashboard_frame = ctk.CTkFrame(self, corner_radius=0, fg_color="transparent")
        
        # Header
        self.header = ctk.CTkLabel(self.dashboard_frame, text="Dashboard", font=ctk.CTkFont(size=24, weight="bold"))
        self.header.grid(row=0, column=0, padx=20, pady=20, sticky="w")

        # Controls
        self.controls_frame = ctk.CTkFrame(self.dashboard_frame)
        self.controls_frame.grid(row=1, column=0, padx=20, pady=10, sticky="ew")
        
        self.status_label = ctk.CTkLabel(self.controls_frame, text="Status: Stopped", text_color="red")
        self.status_label.pack(side="left", padx=20, pady=20)

        self.start_btn = ctk.CTkButton(self.controls_frame, text="Start Scheduler", command=self.toggle_scheduler, fg_color="green")
        self.start_btn.pack(side="left", padx=10, pady=20)

        self.run_now_btn = ctk.CTkButton(self.controls_frame, text="Run Job Once", command=self.run_job_once)
        self.run_now_btn.pack(side="left", padx=10, pady=20)

        self.batch_btn = ctk.CTkButton(self.controls_frame, text="Batch Schedule All", command=self.run_batch_schedule, fg_color="#E07A5F") # Different color
        self.batch_btn.pack(side="left", padx=10, pady=20)


        # Log Box
        self.log_label = ctk.CTkLabel(self.dashboard_frame, text="Application Logs:")
        self.log_label.grid(row=2, column=0, padx=20, pady=(10,0), sticky="w")

        self.log_box = ctk.CTkTextbox(self.dashboard_frame, height=300)
        self.log_box.grid(row=3, column=0, padx=20, pady=(5, 20), sticky="nsew")
        self.dashboard_frame.grid_rowconfigure(3, weight=1)
        self.dashboard_frame.grid_columnconfigure(0, weight=1) # Make log box expand

    def create_queue_page(self):
        self.queue_frame = ctk.CTkFrame(self, corner_radius=0, fg_color="transparent")
        
        self.q_header = ctk.CTkLabel(self.queue_frame, text="Topic Queue", font=ctk.CTkFont(size=24, weight="bold"))
        self.q_header.grid(row=0, column=0, padx=20, pady=20, sticky="w")

        # Input
        self.input_frame = ctk.CTkFrame(self.queue_frame)
        self.input_frame.grid(row=1, column=0, padx=20, pady=10, sticky="ew")
        
        self.topic_input = ctk.CTkTextbox(self.input_frame, height=100, width=400)
        self.topic_input.pack(side="left", padx=10, pady=10)
        self.topic_input.insert("1.0", "Paste a list of topics here (one per line)...")
        # Bind focus in to clear placeholder? CustomTkinter placeholder for Textbox is not built-in same way as Entry.
        # Let's just leave it empty or use a label.
        self.topic_input.delete("1.0", "end") 
        # Actually proper placeholder behavior is complex. Let's just put a label above it or tooltip.
        # But for now, just empty box.
        
        self.add_btn = ctk.CTkButton(self.input_frame, text="Add Topics", command=self.add_topic)
        self.add_btn.pack(side="left", padx=10, pady=10, anchor="n")

        # List
        self.queue_list = ctk.CTkTextbox(self.queue_frame)
        self.queue_list.grid(row=2, column=0, padx=20, pady=10, sticky="nsew")
        
        self.refresh_queue_btn = ctk.CTkButton(self.queue_frame, text="Refresh Queue View", command=self.refresh_queue_display)
        self.refresh_queue_btn.grid(row=3, column=0, padx=20, pady=10)
        
        self.queue_frame.grid_rowconfigure(2, weight=1)
        self.queue_frame.grid_columnconfigure(0, weight=1)

    def create_settings_page(self):
        self.settings_frame = ctk.CTkFrame(self, corner_radius=0, fg_color="transparent")
        
        self.s_header = ctk.CTkLabel(self.settings_frame, text="Settings", font=ctk.CTkFont(size=24, weight="bold"))
        self.s_header.grid(row=0, column=0, padx=20, pady=20, sticky="w")

        # Connect Facebook
        self.auth_frame = ctk.CTkFrame(self.settings_frame)
        self.auth_frame.grid(row=1, column=0, padx=20, pady=10, sticky="ew")

        self.current_page_label = ctk.CTkLabel(self.auth_frame, text=f"Target Page ID: {self.job_manager.data.get('target_page_id', 'Not Set')}")
        self.current_page_label.pack(side="left", padx=20, pady=20)

        self.auth_btn = ctk.CTkButton(self.auth_frame, text="Connect Facebook Page", command=self.start_auth_flow_gui)
        self.auth_btn.pack(side="right", padx=20, pady=20)

        # --- Schedule Interval Settings ---
        self.interval_frame = ctk.CTkFrame(self.settings_frame)
        self.interval_frame.grid(row=2, column=0, padx=20, pady=10, sticky="ew")
        
        ctk.CTkLabel(self.interval_frame, text="Schedule Interval", font=ctk.CTkFont(size=16, weight="bold")).pack(anchor="w", padx=20, pady=(20, 10))

        # Radio Buttons
        self.interval_type_var = ctk.StringVar(value="Hourly")
        self.radio_frame = ctk.CTkFrame(self.interval_frame, fg_color="transparent")
        self.radio_frame.pack(fill="x", padx=20)
        
        self.radio_hourly = ctk.CTkRadioButton(self.radio_frame, text="Hourly", variable=self.interval_type_var, value="Hourly", command=self.update_slider_range)
        self.radio_hourly.pack(side="left", padx=10)
        
        self.radio_daily = ctk.CTkRadioButton(self.radio_frame, text="Daily", variable=self.interval_type_var, value="Daily", command=self.update_slider_range)
        self.radio_daily.pack(side="left", padx=10)
        
        self.radio_weekly = ctk.CTkRadioButton(self.radio_frame, text="Weekly", variable=self.interval_type_var, value="Weekly", command=self.update_slider_range)
        self.radio_weekly.pack(side="left", padx=10)

        # Slider
        self.slider_val_var = ctk.DoubleVar(value=4)
        self.slider = ctk.CTkSlider(self.interval_frame, from_=1, to=24, variable=self.slider_val_var, command=self.on_slider_change)
        self.slider.pack(fill="x", padx=20, pady=20)
        
        self.slider_label = ctk.CTkLabel(self.interval_frame, text="Every 4 Hours")
        self.slider_label.pack(pady=(0, 10))
        
        self.save_interval_btn = ctk.CTkButton(self.interval_frame, text="Save Interval", command=self.save_interval_settings)
        self.save_interval_btn.pack(pady=20)

        # --- AI Model Settings ---
        self.model_frame = ctk.CTkFrame(self.settings_frame)
        self.model_frame.grid(row=3, column=0, padx=20, pady=10, sticky="ew")
        
        ctk.CTkLabel(self.model_frame, text="AI Model (Content Generation)", font=ctk.CTkFont(size=16, weight="bold")).pack(anchor="w", padx=20, pady=(20, 10))
        
        self.model_dropdown = ctk.CTkComboBox(self.model_frame, width=300, values=["Fetching models..."])
        self.model_dropdown.pack(padx=20, pady=10)
        
        # Populate models in background or immediately
        self.populate_model_dropdown()
        
        self.save_model_btn = ctk.CTkButton(self.model_frame, text="Save Model", command=self.save_model_selection)
        self.save_model_btn.pack(pady=20)

        # Initialize UI from current data
        self.load_current_interval_ui()


    def load_current_interval_ui(self):
        curr_mins = self.job_manager.data.get("interval_minutes", 240)
        
        # Determine type
        if curr_mins % 10080 == 0:
            self.interval_type_var.set("Weekly")
            val = curr_mins // 10080
        elif curr_mins % 1440 == 0:
            self.interval_type_var.set("Daily")
            val = curr_mins // 1440
        else:
            self.interval_type_var.set("Hourly")
            val = curr_mins / 60
            
        self.update_slider_range()
        self.slider.set(val)
        self.on_slider_change(val)

    def update_slider_range(self):
        type_ = self.interval_type_var.get()
        if type_ == "Hourly":
            self.slider.configure(from_=1, to=24, number_of_steps=23)
        elif type_ == "Daily":
            self.slider.configure(from_=1, to=7, number_of_steps=6)
        elif type_ == "Weekly":
            self.slider.configure(from_=1, to=52, number_of_steps=51)
        
        # update label immediately
        self.on_slider_change(self.slider.get())

    def on_slider_change(self, value):
        type_ = self.interval_type_var.get()
        val = int(value)
        self.slider_label.configure(text=f"Every {val} {type_[:-2] if val == 1 else type_}") # Hour/Hours, Day/Days? Hourly/Daily is adj.
        # "Every 1 Hourly" -> bad. "Every 1 Hour"
        
        unit = "Hour" if type_ == "Hourly" else "Day" if type_ == "Daily" else "Week"
        if val > 1:
            unit += "s"
        self.slider_label.configure(text=f"Every {val} {unit}")

    def save_interval_settings(self):
        """Saves schedule interval settings."""
        # 1. Calculate minutes
        val = int(round(self.slider_val_var.get()))
        interval_type = self.interval_type_var.get()
        
        minutes = 0
        if interval_type == "Hourly":
            minutes = val * 60
        elif interval_type == "Daily":
            minutes = val * 60 * 24
        elif interval_type == "Weekly":
            minutes = val * 60 * 24 * 7
            
        # 2. Save
        self.job_manager.data["interval_minutes"] = minutes
        self.job_manager.save_data()
        
        if self.scheduler_running:
            self.toggle_scheduler() # Stop
            # Wait a sec? Thread join?
            self.after(1000, self.toggle_scheduler) # Start again

    # --- Logic --- #
    
    def populate_model_dropdown(self):
        """Fetches free models from OpenRouter and populates the dropdown."""
        try:
            from content_engine import ContentEngine
            api_key = self.job_manager.content_engine.openrouter_api_key
            
            # This might be slow, so ideally run in thread, but for now simple:
            models = ContentEngine.get_available_free_models(api_key)
            
            if models:
                model_ids = [m["id"] for m in models]
                self.model_dropdown.configure(values=model_ids)
                
                # Set current selection
                current = self.job_manager.data.get("selected_model")
                if current and current in model_ids:
                    self.model_dropdown.set(current)
                else:
                    self.model_dropdown.set(model_ids[0])
            else:
                 self.model_dropdown.configure(values=["No free models found or API Error"])
        except Exception as e:
            print(f"Error populating models: {e}")
            self.model_dropdown.configure(values=["Error fetching models"])

    def save_model_selection(self):
        """Saves the selected AI model."""
        selected = self.model_dropdown.get()
        if not selected or "Error" in selected or "Fetching" in selected:
            messagebox.showerror("Error", "Invalid model selected.")
            return

        self.job_manager.data["selected_model"] = selected
        self.job_manager.save_data()
        
        # Update current engine instance
        self.job_manager.content_engine.model_id = selected
        
        messagebox.showinfo("Saved", f"Model updated to:\n{selected}")

    def select_frame_by_name(self, name):
        # Update buttons
        self.sidebar_button_1.configure(fg_color=("gray75", "gray25") if name == "dashboard" else "transparent")
        self.sidebar_button_2.configure(fg_color=("gray75", "gray25") if name == "queue" else "transparent")
        self.sidebar_button_3.configure(fg_color=("gray75", "gray25") if name == "settings" else "transparent")

        # Show frame
        if name == "dashboard":
            self.dashboard_frame.grid(row=0, column=1, sticky="nsew")
        else:
            self.dashboard_frame.grid_forget()
            
        if name == "queue":
            self.queue_frame.grid(row=0, column=1, sticky="nsew")
            self.refresh_queue_display()
        else:
            self.queue_frame.grid_forget()

        if name == "settings":
            self.settings_frame.grid(row=0, column=1, sticky="nsew")
        else:
            self.settings_frame.grid_forget()

    def sidebar_button_event(self):
        # Determine which button called
        # Not creating separate callbacks for simplicity
        import traceback
        # In tkinter, events are tricky without lambda logic if using same function
        # But here I assigned distinct commands in __init__, wait, actually I pointed them all here in my logic above?
        # Ah, in create_sidebar I pointed them all to self.sidebar_button_event
        # I need to fix that or check focus. 
        pass

    def sidebar_button_event(self):
        # This is generic, need to know which one?
        # Let's just redefine them to be specific lambdas or functions
        pass 
        
    # Redefine create_sidebar commands properly in __init__? 
    # Or just use lambdas in create_sidebar
    
    # Let's fix create_sidebar commands
    def create_sidebar(self):
        self.sidebar_frame = ctk.CTkFrame(self, width=140, corner_radius=0)
        self.sidebar_frame.grid(row=0, column=0, rowspan=4, sticky="nsew")
        self.sidebar_frame.grid_rowconfigure(4, weight=1)

        self.logo_label = ctk.CTkLabel(self.sidebar_frame, text="FB Poster", font=ctk.CTkFont(size=20, weight="bold"))
        self.logo_label.grid(row=0, column=0, padx=20, pady=(20, 10))

        self.sidebar_button_1 = ctk.CTkButton(self.sidebar_frame, text="Dashboard", command=lambda: self.select_frame_by_name("dashboard"))
        self.sidebar_button_1.grid(row=1, column=0, padx=20, pady=10)

        self.sidebar_button_2 = ctk.CTkButton(self.sidebar_frame, text="Queue", command=lambda: self.select_frame_by_name("queue"))
        self.sidebar_button_2.grid(row=2, column=0, padx=20, pady=10)

        self.sidebar_button_3 = ctk.CTkButton(self.sidebar_frame, text="Settings", command=lambda: self.select_frame_by_name("settings"))
        self.sidebar_button_3.grid(row=3, column=0, padx=20, pady=10)

        # Exit Button at bottom
        self.exit_button = ctk.CTkButton(self.sidebar_frame, text="Exit", fg_color="darkred", hover_color="red", command=self.close_app)
        self.exit_button.grid(row=5, column=0, padx=20, pady=20)

    def toggle_scheduler(self):
        if self.scheduler_running:
            self.scheduler_running = False
            self.status_label.configure(text="Status: Stopping...", text_color="orange")
            print("Stopping scheduler...")
            self.start_btn.configure(text="Start Scheduler", fg_color="green")
        else:
            self.scheduler_running = True
            self.status_label.configure(text="Status: Running", text_color="green")
            self.start_btn.configure(text="Stop Scheduler", fg_color="red")
            print("Starting scheduler...")
            
            # Start thread
            self.scheduler_thread = threading.Thread(target=self.run_scheduler_loop)
            self.scheduler_thread.daemon = True
            self.scheduler_thread.start()

    def run_scheduler_loop(self):
        interval = self.job_manager.data.get("interval_minutes", 240)
        schedule.every(interval).minutes.do(self.job_manager.run_job)
        
        while self.scheduler_running:
            schedule.run_pending()
            time.sleep(1)
            
        schedule.clear()
        
        # Update UI from main thread? ctk handles basic calls usually, but best to be safe.
        # Since we're accessing self.status_label in toggle_scheduler we should be fine if we set state there.
        # But we need to update status to Stopped when loop finishes
        self.status_label.configure(text="Status: Stopped", text_color="red")
        print("Scheduler stopped.")

    def run_job_once(self):
        threading.Thread(target=self.job_manager.run_job).start()

    def run_batch_schedule(self):
        # Ask for confirmation? CustomTkinter doesn't have simple dialogs built-in easily without pip install custom dialogs or using tk.
        # Let's just run it for now, or use tk.messagebox
        if messagebox.askyesno("Confirm Batch", "This will process all items in the queue and schedule them on Facebook. Continue?"):
            threading.Thread(target=self.job_manager.batch_schedule).start()


    def add_topic(self):
        raw_text = self.topic_input.get("1.0", "end")
        lines = raw_text.split('\n')
        
        added_count = 0
        for line in lines:
            line = line.strip()
            if line:
                self.job_manager.add_topic(line)
                added_count += 1
                
        if added_count > 0:
            self.topic_input.delete("1.0", "end")
            self.refresh_queue_display()
            # self.status_label.configure(text=f"Added {added_count} topics.", text_color="green") 
            # (If I had access to status label easily here, but it's on dashboard)
            print(f"Bulk added {added_count} topics.")
        else:
             messagebox.showwarning("Empty", "Please enter at least one topic.")

    def close_app(self):
        """Cleanly closes the application."""
        if self.scheduler_running:
            self.scheduler_running = False
            # We don't wait for join here to avoid freezing UI if thread is sleeping, 
            # but daemon=True will kill it anyway.
            
        self.destroy()
        print("Application closed.")

    def refresh_queue_display(self):
        self.job_manager.data = self.job_manager.load_data() # Reload from file
        queue = self.job_manager.data.get("topics_queue", [])
        
        self.queue_list.configure(state="normal")
        self.queue_list.delete("1.0", "end")
        for i, topic in enumerate(queue):
            self.queue_list.insert("end", f"{i+1}. {topic}\n")
        self.queue_list.configure(state="disabled")

    def start_auth_flow_gui(self):
        # Run auth in thread
        threading.Thread(target=self._auth_worker).start()

    def _auth_worker(self):
        print("Starting Authentication Flow...")
        try:
            auth = FacebookAuth()
            print("Please check your browser to approve the app.")
            code = auth.start_auth_flow()
            if not code:
                print("Auth failed or cancelled.")
                return
            
            print("Exchanging tokens...")
            auth.exchange_code_for_token()
            auth.get_long_lived_token()
            pages = auth.get_user_pages()
            
            if not pages:
                print("No pages found.")
                return

            # Since we can't easily do input() here, we'll just pick the first page or pop up a dialog
            # For V1, let's just pick the first page automatically or ask user via dialog if possible.
            # tkinter MessageBox is modal, but we are in a thread... NOT SAFE.
            # Logic: Just pick the first one and print info.
            
            selected_page = pages[0]
            print(f"Automatically selected first page: {selected_page['name']}")
            
            self.job_manager.set_target_page(selected_page['id'], selected_page['access_token'])
            self.current_page_label.configure(text=f"Target Page ID: {selected_page['id']}")
            print("Setup Complete!")
            
        except Exception as e:
            print(f"Auth Error: {e}")

if __name__ == "__main__":
    app = App()
    app.mainloop()
