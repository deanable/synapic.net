import schedule
import time
from auth import FacebookAuth
from scheduler_job import JobManager
from config import Config

def main():
    print("=== Facebook Auto-Poster Started ===")
    
    # Validation
    try:
        Config.validate()
    except ValueError as e:
        print(f"Configuration Error: {e}")
        print("Please check your .env file.")
        return

    job_manager = JobManager()
    
    # 1. Authentication & Setup
    if not job_manager.data.get("target_page_token"):
        print("\n--- Setup Required ---")
        auth = FacebookAuth()
        
        print("Starting OAuth flow...")
        code = auth.start_auth_flow()
        if not code:
            print("Authentication failed.")
            return

        print("Exchanging tokens...")
        auth.exchange_code_for_token()
        auth.get_long_lived_token()
        
        pages = auth.get_user_pages()
        if not pages:
            print("No pages found for this user.")
            return

        print("\nAvailable Pages:")
        for idx, page in enumerate(pages):
            print(f"{idx + 1}. {page['name']} (ID: {page['id']})")

        while True:
            try:
                choice = int(input("\nSelect the page number to manage: "))
                if 1 <= choice <= len(pages):
                    selected_page = pages[choice - 1]
                    break
                else:
                    print("Invalid choice.")
            except ValueError:
                print("Please enter a number.")

        print(f"Selected: {selected_page['name']}")
        job_manager.set_target_page(selected_page['id'], selected_page['access_token'])
        print("Setup complete! Credentials saved locally.")
    else:
        print("Loaded existing page credentials from queue.json.")

    # 2. Topic Management
    print("\n--- Queue Management ---")
    current_q_len = len(job_manager.data.get("topics_queue", []))
    print(f"Current topics in queue: {current_q_len}")
    
    while True:
        choice = input("Add topics to queue? (y/n): ").lower()
        if choice == 'y':
            topic = input("Enter topic (or 'done' to finish): ")
            if topic == 'done':
                break
            job_manager.add_topic(topic)
        elif choice == 'n':
            break

    # 3. Schedule
    interval = job_manager.data.get("interval_minutes", 240)
    print(f"\n--- Starting Scheduler ---")
    print(f"Posting every {interval} minutes.")
    print("Press Ctrl+C to stop.")

    # Run once immediately?
    # job_manager.run_job() 

    schedule.every(interval).minutes.do(job_manager.run_job)

    while True:
        try:
            schedule.run_pending()
            time.sleep(1)
        except KeyboardInterrupt:
            print("\nStopping scheduler.")
            break

if __name__ == "__main__":
    main()
